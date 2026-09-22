using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantFiscalVorgang(
    string Id,
    DateTimeOffset StartedAt);

/// <summary>
/// Restaurant-specific Bestellung-V1 bridge. It reuses the common
/// TseVorgangService so Restaurant does not invent a second TSE engine.
/// Each newly captured table position is secured as its own immutable
/// Bestellung record. TSE outage is recorded but never deletes the order.
/// </summary>
public sealed class RestaurantFiscalOrderService
{
    private readonly SqliteDatabase _db;
    private readonly TseVorgangService _vorgaenge;

    public RestaurantFiscalOrderService(
        SqliteDatabase db,
        TseVorgangService vorgaenge)
    {
        _db = db;
        _vorgaenge = vorgaenge;
    }

    public async Task<RestaurantFiscalVorgang> BeginChangeAsync(
        string sessionId,
        string actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));

        var id = "restaurant-" + Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.Now;

        await _vorgaenge.StartAsync(
            id,
            training: false,
            startedAt,
            actor,
            ct);

        return new RestaurantFiscalVorgang(id, startedAt);
    }

    public Task AbortChangeAsync(
        RestaurantFiscalVorgang vorgang,
        string actor,
        CancellationToken ct = default) =>
        _vorgaenge.AbortAsync(
            vorgang.Id,
            Array.Empty<CartLine>(),
            0,
            actor,
            actor,
            ct);

    public async Task SecureAddedItemAsync(
        string sessionId,
        RestaurantSessionItem item,
        RestaurantFiscalVorgang vorgang,
        string actor,
        CancellationToken ct = default)
    {
        if (item.SessionId != sessionId)
            throw new InvalidOperationException(
                "Restaurant-Position gehört nicht zum erwarteten Tischvorgang.");

        var line = ToCartLine(item);
        var processData = FiscalProcessData.BestellungText(new[] { line });

        var result = await _vorgaenge.FinishAsync(
            vorgang.Id,
            FiscalProcessData.BestellungProcessType,
            processData,
            actor,
            $"RESTAURANT:{sessionId}",
            ct);

        await InsertRecordAsync(
            sessionId,
            vorgang.StartedAt,
            actor,
            new[] { line },
            result,
            ct);
    }

    public async Task SecureMergeAsync(
        string sourceSessionId,
        string targetSessionId,
        IReadOnlyList<RestaurantSessionItem> movedItems,
        RestaurantFiscalVorgang sourceVorgang,
        RestaurantFiscalVorgang targetVorgang,
        string actor,
        CancellationToken ct = default)
    {
        if (movedItems.Count == 0)
            throw new InvalidOperationException("Keine offenen Positionen zum Zusammenlegen.");

        var positive = movedItems.Select(ToCartLine).ToArray();
        var negative = positive.Select(line => new CartLine
        {
            ProductId = line.ProductId,
            ProductName = line.ProductName,
            VariantName = line.VariantName,
            Quantity = -line.Quantity,
            Unit = line.Unit,
            UnitPriceCents = line.UnitPriceCents,
            ListUnitPriceCents = line.ListUnitPriceCents,
            VatRate = line.VatRate,
            PfandCents = line.PfandCents
        }).ToArray();

        var sourceResult = await _vorgaenge.FinishAsync(
            sourceVorgang.Id,
            FiscalProcessData.BestellungProcessType,
            FiscalProcessData.BestellungText(negative),
            actor,
            $"RESTAURANT:{sourceSessionId}",
            ct);

        var targetResult = await _vorgaenge.FinishAsync(
            targetVorgang.Id,
            FiscalProcessData.BestellungProcessType,
            FiscalProcessData.BestellungText(positive),
            actor,
            $"RESTAURANT:{targetSessionId}",
            ct);

        await InsertMergeRecordsAsync(
            sourceSessionId,
            targetSessionId,
            sourceVorgang.StartedAt,
            targetVorgang.StartedAt,
            actor,
            negative,
            positive,
            sourceResult,
            targetResult,
            ct);
    }

    public async Task<bool> IsCurrentStateSecuredAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();

            var open = new Dictionary<string,long>(StringComparer.Ordinal);
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT product_id,product_name,unit_price_cents,vat_rate,pfand_cents,
                           SUM(quantity_milli)
                    FROM restaurant_session_items
                    WHERE session_id=$session AND state IN ('ACTIVE','PAID')
                    GROUP BY product_id,product_name,unit_price_cents,vat_rate,pfand_cents;
                    """;
                q.Parameters.AddWithValue("$session", sessionId);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    open[Key(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetInt64(2),
                        Convert.ToDecimal(r.GetDouble(3)),
                        r.GetInt64(4))] = r.GetInt64(5);
                }
            }

            var secured = new Dictionary<string,long>(StringComparer.Ordinal);
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT i.product_id,i.product_name,i.unit_price_cents,i.vat_rate,i.pfand_cents,
                           SUM(i.quantity_milli)
                    FROM restaurant_bestellung_items i
                    JOIN restaurant_bestellungen b ON b.id=i.bestellung_id
                    WHERE b.session_id=$session
                    GROUP BY i.product_id,i.product_name,i.unit_price_cents,i.vat_rate,i.pfand_cents;
                    """;
                q.Parameters.AddWithValue("$session", sessionId);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    secured[Key(
                        r.GetInt64(0),
                        r.GetString(1),
                        r.GetInt64(2),
                        Convert.ToDecimal(r.GetDouble(3)),
                        r.GetInt64(4))] = r.GetInt64(5);
                }
            }

            if (open.Count != secured.Count)
                return false;

            foreach (var pair in open)
            {
                if (!secured.TryGetValue(pair.Key, out var quantity) ||
                    quantity != pair.Value)
                {
                    return false;
                }
            }

            return true;
        });
    }

    private async Task InsertRecordAsync(
        string sessionId,
        DateTimeOffset startedAt,
        string actor,
        IReadOnlyList<CartLine> lines,
        SaleTseResult result,
        CancellationToken ct)
    {
        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            int sequence;
            await using (var seq = c.CreateCommand())
            {
                seq.Transaction = tx;
                seq.CommandText = """
                    SELECT COALESCE(MAX(sequence),0)+1
                    FROM restaurant_bestellungen
                    WHERE session_id=$session;
                    """;
                seq.Parameters.AddWithValue("$session", sessionId);
                sequence = Convert.ToInt32(await seq.ExecuteScalarAsync(ct));
            }

            var kind = sequence == 1 ? "ANNAHME" : "AENDERUNG";
            var now = DateTimeOffset.Now;
            long id;

            await using (var head = c.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = """
                    INSERT INTO restaurant_bestellungen(
                        session_id,sequence,kind,started_at,created_at,operator_name,total_cents,
                        client_id,transaction_number,signature_counter,serial_number,signature,
                        start_log_time,log_time,outage,outage_reason)
                    VALUES(
                        $session,$sequence,$kind,$started,$created,$operator,$total,
                        $client,$transaction,$counter,$serial,$signature,
                        $startLog,$log,$outage,$reason)
                    RETURNING id;
                    """;
                head.Parameters.AddWithValue("$session", sessionId);
                head.Parameters.AddWithValue("$sequence", sequence);
                head.Parameters.AddWithValue("$kind", kind);
                head.Parameters.AddWithValue("$started", startedAt.ToString("O"));
                head.Parameters.AddWithValue("$created", now.ToString("O"));
                head.Parameters.AddWithValue("$operator", actor ?? "");
                head.Parameters.AddWithValue("$total", lines.Sum(x => x.LineTotalCents));
                head.Parameters.AddWithValue("$client", result.ClientId);
                head.Parameters.AddWithValue("$transaction", result.TransactionNumber);
                head.Parameters.AddWithValue("$counter", result.SignatureCounter);
                head.Parameters.AddWithValue("$serial", result.SerialNumber);
                head.Parameters.AddWithValue("$signature", result.Signature);
                head.Parameters.AddWithValue("$startLog", result.StartLogTime?.ToString("O") ?? "");
                head.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
                head.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
                head.Parameters.AddWithValue("$reason", result.OutageMessage);
                id = Convert.ToInt64(await head.ExecuteScalarAsync(ct));
            }

            foreach (var line in lines)
            {
                await using var item = c.CreateCommand();
                item.Transaction = tx;
                item.CommandText = """
                    INSERT INTO restaurant_bestellung_items(
                        bestellung_id,product_id,product_name,quantity_milli,
                        unit_price_cents,vat_rate,pfand_cents)
                    VALUES($bestellung,$product,$name,$quantity,$price,$vat,$pfand);
                    """;
                item.Parameters.AddWithValue("$bestellung", id);
                item.Parameters.AddWithValue("$product", line.ProductId);
                item.Parameters.AddWithValue("$name", line.ProductName);
                item.Parameters.AddWithValue("$quantity", QuantityStorage.ToMilli(line.Quantity));
                item.Parameters.AddWithValue("$price", line.UnitPriceCents);
                item.Parameters.AddWithValue("$vat", (double)line.VatRate);
                item.Parameters.AddWithValue("$pfand", line.PfandCents);
                await item.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }

    private async Task InsertMergeRecordsAsync(
        string sourceSessionId,
        string targetSessionId,
        DateTimeOffset sourceStartedAt,
        DateTimeOffset targetStartedAt,
        string actor,
        IReadOnlyList<CartLine> sourceLines,
        IReadOnlyList<CartLine> targetLines,
        SaleTseResult sourceResult,
        SaleTseResult targetResult,
        CancellationToken ct)
    {
        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            async Task InsertOne(
                string sessionId,
                DateTimeOffset startedAt,
                IReadOnlyList<CartLine> lines,
                SaleTseResult result)
            {
                int sequence;
                await using (var seq = c.CreateCommand())
                {
                    seq.Transaction = tx;
                    seq.CommandText = """
                        SELECT COALESCE(MAX(sequence),0)+1
                        FROM restaurant_bestellungen
                        WHERE session_id=$session;
                        """;
                    seq.Parameters.AddWithValue("$session", sessionId);
                    sequence = Convert.ToInt32(await seq.ExecuteScalarAsync(ct));
                }

                var now = DateTimeOffset.Now;
                long id;
                await using (var head = c.CreateCommand())
                {
                    head.Transaction = tx;
                    head.CommandText = """
                        INSERT INTO restaurant_bestellungen(
                            session_id,sequence,kind,started_at,created_at,operator_name,total_cents,
                            client_id,transaction_number,signature_counter,serial_number,signature,
                            start_log_time,log_time,outage,outage_reason)
                        VALUES(
                            $session,$sequence,'AENDERUNG',$started,$created,$operator,$total,
                            $client,$transaction,$counter,$serial,$signature,
                            $startLog,$log,$outage,$reason)
                        RETURNING id;
                        """;
                    head.Parameters.AddWithValue("$session", sessionId);
                    head.Parameters.AddWithValue("$sequence", sequence);
                    head.Parameters.AddWithValue("$started", startedAt.ToString("O"));
                    head.Parameters.AddWithValue("$created", now.ToString("O"));
                    head.Parameters.AddWithValue("$operator", actor ?? "");
                    head.Parameters.AddWithValue("$total", lines.Sum(x => x.LineTotalCents));
                    head.Parameters.AddWithValue("$client", result.ClientId);
                    head.Parameters.AddWithValue("$transaction", result.TransactionNumber);
                    head.Parameters.AddWithValue("$counter", result.SignatureCounter);
                    head.Parameters.AddWithValue("$serial", result.SerialNumber);
                    head.Parameters.AddWithValue("$signature", result.Signature);
                    head.Parameters.AddWithValue("$startLog", result.StartLogTime?.ToString("O") ?? "");
                    head.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
                    head.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
                    head.Parameters.AddWithValue("$reason", result.OutageMessage);
                    id = Convert.ToInt64(await head.ExecuteScalarAsync(ct));
                }

                foreach (var line in lines)
                {
                    await using var item = c.CreateCommand();
                    item.Transaction = tx;
                    item.CommandText = """
                        INSERT INTO restaurant_bestellung_items(
                            bestellung_id,product_id,product_name,quantity_milli,
                            unit_price_cents,vat_rate,pfand_cents)
                        VALUES($bestellung,$product,$name,$quantity,$price,$vat,$pfand);
                        """;
                    item.Parameters.AddWithValue("$bestellung", id);
                    item.Parameters.AddWithValue("$product", line.ProductId);
                    item.Parameters.AddWithValue("$name", line.ProductName);
                    item.Parameters.AddWithValue("$quantity", QuantityStorage.ToMilli(line.Quantity));
                    item.Parameters.AddWithValue("$price", line.UnitPriceCents);
                    item.Parameters.AddWithValue("$vat", (double)line.VatRate);
                    item.Parameters.AddWithValue("$pfand", line.PfandCents);
                    await item.ExecuteNonQueryAsync(ct);
                }
            }

            await InsertOne(sourceSessionId, sourceStartedAt, sourceLines, sourceResult);
            await InsertOne(targetSessionId, targetStartedAt, targetLines, targetResult);
            await tx.CommitAsync(ct);
        });
    }

    private static CartLine ToCartLine(RestaurantSessionItem item) =>
        new()
        {
            ProductId = item.ProductId,
            ProductName = item.ProductName,
            VariantName = item.VariantName,
            Quantity = item.Quantity,
            Unit = "Stück",
            UnitPriceCents = item.UnitPriceCents,
            ListUnitPriceCents = item.UnitPriceCents,
            VatRate = item.VatRate,
            PfandCents = item.PfandCents
        };

    private static string Key(
        long productId,
        string name,
        long unitPriceCents,
        decimal vatRate,
        long pfandCents) =>
        string.Join(
            "|",
            productId,
            name,
            unitPriceCents,
            vatRate.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            pfandCents);
}
