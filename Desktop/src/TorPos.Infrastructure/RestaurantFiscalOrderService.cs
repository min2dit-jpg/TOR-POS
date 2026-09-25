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
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

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

        await _vorgaenge.TagReferenceAsync(
            id,
            $"RESTAURANT:{sessionId}",
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

    public async Task<bool> AbortPendingAddedItemAsync(
        RestaurantFiscalVorgang vorgang,
        RestaurantSessionItem item,
        string actor,
        CancellationToken ct = default)
    {
        await _vorgaenge.AbortAsync(
            vorgang.Id,
            new[] { ToCartLine(item) },
            0,
            actor,
            actor,
            ct);

        var state = await _vorgaenge.GetAsync(
            vorgang.Id,
            ct);

        return state?.State == TseVorgangService.Aborted;
    }

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

        // Crash/retry safety: once the table line is SECURED, its immutable
        // Bestellung already exists. A replay must not touch the TSE again.
        if (await IsItemSecuredAsync(
                sessionId,
                item.Id,
                ct))
        {
            return;
        }

        var line = ToCartLine(item);
        var processData = FiscalProcessData.BestellungText(new[] { line });

        // F-6 journals a successful TSE Finish before the Restaurant record is
        // persisted. Reuse that exact result after a crash instead of finishing
        // the same transaction again. If the Vorgang is already terminal but
        // its journal is missing, fail closed as a documented outage; never
        // create a second payment/order-time TSE transaction on recovery.
        var result =
            await _vorgaenge.GetJournaledFinishAsync(
                vorgang.Id,
                ct);

        if (result is null)
        {
            var state =
                await _vorgaenge.GetAsync(
                    vorgang.Id,
                    ct);

            if (state is not null &&
                state.State is
                    TseVorgangService.Finished or
                    TseVorgangService.Aborted)
            {
                result = SaleTseResult.Outage(
                    "Restaurant-Bestellung wurde vor dem Persistieren bereits fiskalisch beendet; " +
                    "kein wiederverwendbares TSE-Finish-Journal vorhanden. " +
                    "Keine zweite TSE-Transaktion erzeugt.");
            }
            else
            {
                result = await _vorgaenge.FinishAsync(
                    vorgang.Id,
                    FiscalProcessData.BestellungProcessType,
                    processData,
                    actor,
                    $"RESTAURANT:{sessionId}",
                    ct);
            }
        }

        await InsertRecordAsync(
            sessionId,
            vorgang.StartedAt,
            actor,
            new[] { line },
            result,
            ct,
            consumedVorgangId: vorgang.Id,
            securedItemId: item.Id);
    }

    public async Task SecureCancelledItemAsync(
        string sessionId,
        RestaurantSessionItem cancelledItem,
        RestaurantFiscalVorgang vorgang,
        string actor,
        CancellationToken ct = default)
    {
        if (!string.Equals(
                cancelledItem.SessionId,
                sessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Stornierte Restaurant-Position gehört nicht zum erwarteten Tischvorgang.");
        }

        var original = ToCartLine(cancelledItem);
        var reversal = new CartLine
        {
            ProductId = original.ProductId,
            ProductName = original.ProductName,
            VariantName = original.VariantName,
            Quantity = -original.Quantity,
            Unit = original.Unit,
            UnitPriceCents = original.UnitPriceCents,
            ListUnitPriceCents = original.ListUnitPriceCents,
            VatRate = original.VatRate,
            PfandCents = original.PfandCents
        };

        var result = await _vorgaenge.FinishAsync(
            vorgang.Id,
            FiscalProcessData.BestellungProcessType,
            FiscalProcessData.BestellungText(new[] { reversal }),
            actor,
            $"RESTAURANT:{sessionId}",
            ct);

        await InsertRecordAsync(
            sessionId,
            vorgang.StartedAt,
            actor,
            new[] { reversal },
            result,
            ct,
            consumedVorgangId: vorgang.Id);
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
            sourceVorgang.Id,
            targetVorgang.Id,
            sourceVorgang.StartedAt,
            targetVorgang.StartedAt,
            actor,
            negative,
            positive,
            sourceResult,
            targetResult,
            ct);
    }

    public async Task<IReadOnlyList<string>> ListUnsecuredSessionIdsAsync(
        CancellationToken ct = default)
    {
        var candidates = new List<string>();
        await using (var c = _db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id
                FROM restaurant_sessions
                WHERE state IN ('OPEN','CHECK_REQUESTED','CANCELLED','CLOSED')
                ORDER BY updated_at,id;
                """;

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                candidates.Add(r.GetString(0));
        }

        var result = new List<string>();
        foreach (var sessionId in candidates)
        {
            if (!await IsCurrentStateSecuredAsync(sessionId, ct))
                result.Add(sessionId);
        }

        return result;
    }

    public async Task<bool> ReconcileSessionAsync(
        string sessionId,
        string actor,
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (sessionId.Length == 0)
            throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));

        await _reconcileGate.WaitAsync(ct);
        try
        {
            if (await IsCurrentStateSecuredAsync(sessionId, ct))
                return true;

            var delta = await BuildReconciliationDeltaAsync(sessionId, ct);
            if (delta.Count == 0)
            {
                await MarkPendingSessionItemsSecuredAsync(sessionId, ct);
                return await IsCurrentStateSecuredAsync(sessionId, ct);
            }

            var recovery = await FindRecoveryVorgangAsync(sessionId, ct);
            RestaurantFiscalVorgang vorgang;
            SaleTseResult result;

            if (recovery is not null)
            {
                vorgang = new RestaurantFiscalVorgang(
                    recovery.Id,
                    recovery.StartedAt);

                result =
                    await _vorgaenge.GetJournaledFinishAsync(
                        recovery.Id,
                        ct)
                    ?? (
                        recovery.FinishAttempted ||
                        recovery.State == TseVorgangService.Finished
                            ? SaleTseResult.Outage(
                                "Restaurant-Änderung wurde möglicherweise bereits an der TSE beendet, " +
                                "aber das F-6-Finish-Journal fehlt. Keine zweite TSE-Transaktion erzeugt.")
                            : await _vorgaenge.FinishAsync(
                                recovery.Id,
                                FiscalProcessData.BestellungProcessType,
                                FiscalProcessData.BestellungText(delta),
                                actor,
                                $"RESTAURANT:{sessionId}",
                                ct));
            }
            else
            {
                vorgang = await BeginChangeAsync(
                    sessionId,
                    actor,
                    ct);

                result = await _vorgaenge.FinishAsync(
                    vorgang.Id,
                    FiscalProcessData.BestellungProcessType,
                    FiscalProcessData.BestellungText(delta),
                    actor,
                    $"RESTAURANT:{sessionId}",
                    ct);
            }

            await InsertRecordAsync(
                sessionId,
                vorgang.StartedAt,
                actor,
                delta,
                result,
                ct,
                consumedVorgangId: vorgang.Id,
                securePendingSessionItems: true);

            return await IsCurrentStateSecuredAsync(sessionId, ct);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task<IReadOnlyList<CartLine>> BuildReconciliationDeltaAsync(
        string sessionId,
        CancellationToken ct)
    {
        var current =
            new Dictionary<string, (CartLine Line, long QuantityMilli)>(
                StringComparer.Ordinal);
        var secured =
            new Dictionary<string, (CartLine Line, long QuantityMilli)>(
                StringComparer.Ordinal);

        await using var c = _db.OpenReadConnection();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT product_id,product_name,unit_price_cents,vat_rate,pfand_cents,
                       SUM(quantity_milli)
                FROM restaurant_session_items
                WHERE session_id=$session
                  AND state IN ('ACTIVE','PAID')
                GROUP BY product_id,product_name,unit_price_cents,vat_rate,pfand_cents
                HAVING SUM(quantity_milli)<>0;
                """;
            q.Parameters.AddWithValue("$session", sessionId);

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var line = ReconciliationLine(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetInt64(2),
                    Convert.ToDecimal(r.GetDouble(3)),
                    r.GetInt64(4));
                current[Key(
                    line.ProductId,
                    line.ProductName,
                    line.UnitPriceCents,
                    line.VatRate,
                    line.PfandCents)] =
                    (line, r.GetInt64(5));
            }
        }

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT i.product_id,i.product_name,i.unit_price_cents,i.vat_rate,i.pfand_cents,
                       SUM(i.quantity_milli)
                FROM restaurant_bestellung_items i
                JOIN restaurant_bestellungen b ON b.id=i.bestellung_id
                WHERE b.session_id=$session
                GROUP BY i.product_id,i.product_name,i.unit_price_cents,i.vat_rate,i.pfand_cents
                HAVING SUM(i.quantity_milli)<>0;
                """;
            q.Parameters.AddWithValue("$session", sessionId);

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var line = ReconciliationLine(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetInt64(2),
                    Convert.ToDecimal(r.GetDouble(3)),
                    r.GetInt64(4));
                secured[Key(
                    line.ProductId,
                    line.ProductName,
                    line.UnitPriceCents,
                    line.VatRate,
                    line.PfandCents)] =
                    (line, r.GetInt64(5));
            }
        }

        var keys = current.Keys
            .Concat(secured.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);

        var result = new List<CartLine>();
        foreach (var key in keys)
        {
            var currentQuantity =
                current.TryGetValue(key, out var currentValue)
                    ? currentValue.QuantityMilli
                    : 0L;
            var securedQuantity =
                secured.TryGetValue(key, out var securedValue)
                    ? securedValue.QuantityMilli
                    : 0L;
            var difference = currentQuantity - securedQuantity;
            if (difference == 0)
                continue;

            var template =
                current.TryGetValue(key, out currentValue)
                    ? currentValue.Line
                    : securedValue.Line;

            result.Add(
                new CartLine(template)
                {
                    Quantity = QuantityStorage.FromMilli(difference)
                });
        }

        return result;
    }

    private async Task<RestaurantFiscalRecoveryVorgang?> FindRecoveryVorgangAsync(
        string sessionId,
        CancellationToken ct)
    {
        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT v.id,v.started_at,v.state,
                   CASE WHEN v.finish_attempted_at<>'' THEN 1 ELSE 0 END,
                   CASE WHEN v.finish_result_json<>'' THEN 1 ELSE 0 END
            FROM tse_vorgaenge v
            WHERE v.reference=$reference
              AND v.state IN ('OPEN','FINISHED')
              AND NOT EXISTS (
                  SELECT 1
                  FROM restaurant_bestellungen b
                  WHERE b.session_id=$session
                    AND v.transaction_number<>''
                    AND b.transaction_number=v.transaction_number)
            ORDER BY v.updated_at DESC,v.started_at DESC
            LIMIT 1;
            """;
        q.Parameters.AddWithValue(
            "$reference",
            $"RESTAURANT:{sessionId}");
        q.Parameters.AddWithValue(
            "$session",
            sessionId);

        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new RestaurantFiscalRecoveryVorgang(
            r.GetString(0),
            DateTimeOffset.Parse(r.GetString(1)),
            r.GetString(2),
            r.GetInt64(3) == 1,
            r.GetInt64(4) == 1);
    }

    private Task MarkPendingSessionItemsSecuredAsync(
        string sessionId,
        CancellationToken ct) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_session_items
                SET fiscal_state='SECURED',
                    version=version+1
                WHERE session_id=$session
                  AND state IN ('ACTIVE','PAID')
                  AND fiscal_state='PENDING';
                """;
            q.Parameters.AddWithValue("$session", sessionId);
            await q.ExecuteNonQueryAsync(ct);
        });

    private static CartLine ReconciliationLine(
        long productId,
        string productName,
        long unitPriceCents,
        decimal vatRate,
        long pfandCents) =>
        new()
        {
            ProductId = productId,
            ProductName = productName,
            Quantity = 0m,
            Unit = "Stück",
            UnitPriceCents = unitPriceCents,
            ListUnitPriceCents = unitPriceCents,
            VatRate = vatRate,
            PfandCents = pfandCents
        };

    private sealed record RestaurantFiscalRecoveryVorgang(
        string Id,
        DateTimeOffset StartedAt,
        string State,
        bool FinishAttempted,
        bool HasJournal);

    public async Task<bool> IsCurrentStateSecuredAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();

            await using (var pending = c.CreateCommand())
            {
                pending.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_session_items
                    WHERE session_id=$session
                      AND state IN ('ACTIVE','PAID')
                      AND fiscal_state='PENDING';
                    """;
                pending.Parameters.AddWithValue("$session", sessionId);

                if (Convert.ToInt32(
                        await pending.ExecuteScalarAsync(ct)) > 0)
                {
                    return false;
                }
            }

            var open = new Dictionary<string,long>(StringComparer.Ordinal);
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT product_id,product_name,unit_price_cents,vat_rate,pfand_cents,
                           SUM(quantity_milli)
                    FROM restaurant_session_items
                    WHERE session_id=$session AND state IN ('ACTIVE','PAID')
                    GROUP BY product_id,product_name,unit_price_cents,vat_rate,pfand_cents
                    HAVING SUM(quantity_milli)<>0;
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
                    GROUP BY i.product_id,i.product_name,i.unit_price_cents,i.vat_rate,i.pfand_cents
                    HAVING SUM(i.quantity_milli)<>0;
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

    private async Task<bool> IsItemSecuredAsync(
        string sessionId,
        long itemId,
        CancellationToken ct)
    {
        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT fiscal_state
            FROM restaurant_session_items
            WHERE id=$item
              AND session_id=$session
            LIMIT 1;
            """;
        q.Parameters.AddWithValue("$item", itemId);
        q.Parameters.AddWithValue("$session", sessionId);

        var state =
            Convert.ToString(
                await q.ExecuteScalarAsync(ct));

        return string.Equals(
            state,
            "SECURED",
            StringComparison.Ordinal);
    }

    private async Task InsertRecordAsync(
        string sessionId,
        DateTimeOffset startedAt,
        string actor,
        IReadOnlyList<CartLine> lines,
        SaleTseResult result,
        CancellationToken ct,
        string consumedVorgangId,
        long? securedItemId = null,
        bool securePendingSessionItems = false)
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

            if (securedItemId is { } itemId)
            {
                await using var secure = c.CreateCommand();
                secure.Transaction = tx;
                secure.CommandText = """
                    UPDATE restaurant_session_items
                    SET fiscal_state='SECURED',
                        version=version+1
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE'
                      AND fiscal_state='PENDING';
                    """;
                secure.Parameters.AddWithValue("$item", itemId);
                secure.Parameters.AddWithValue("$session", sessionId);

                if (await secure.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new InvalidOperationException(
                        "Restaurant-Position konnte nicht atomar als fiskalisch gesichert markiert werden.");
                }
            }

            if (securePendingSessionItems)
            {
                await using var securePending = c.CreateCommand();
                securePending.Transaction = tx;
                securePending.CommandText = """
                    UPDATE restaurant_session_items
                    SET fiscal_state='SECURED',
                        version=version+1
                    WHERE session_id=$session
                      AND state IN ('ACTIVE','PAID')
                      AND fiscal_state='PENDING';
                    """;
                securePending.Parameters.AddWithValue("$session", sessionId);
                await securePending.ExecuteNonQueryAsync(ct);
            }

            await ConsumeRestaurantVorgangAsync(
                c,
                tx,
                consumedVorgangId,
                sessionId,
                ct);

            await tx.CommitAsync(ct);
        });
    }

    private async Task InsertMergeRecordsAsync(
        string sourceSessionId,
        string targetSessionId,
        string sourceVorgangId,
        string targetVorgangId,
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
                            $session,$sequence,$kind,$started,$created,$operator,$total,
                            $client,$transaction,$counter,$serial,$signature,
                            $startLog,$log,$outage,$reason)
                        RETURNING id;
                        """;
                    head.Parameters.AddWithValue("$session", sessionId);
                    head.Parameters.AddWithValue("$sequence", sequence);
                    head.Parameters.AddWithValue("$kind", sequence == 1 ? "ANNAHME" : "AENDERUNG");
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

            await ConsumeRestaurantVorgangAsync(
                c,
                tx,
                sourceVorgangId,
                sourceSessionId,
                ct);
            await ConsumeRestaurantVorgangAsync(
                c,
                tx,
                targetVorgangId,
                targetSessionId,
                ct);

            await tx.CommitAsync(ct);
        });
    }

    private static async Task ConsumeRestaurantVorgangAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string vorgangId,
        string sessionId,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            UPDATE tse_vorgaenge
            SET finish_result_json='',
                reference=$reference,
                updated_at=$now
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", vorgangId);
        q.Parameters.AddWithValue(
            "$reference",
            $"RESTAURANT-COMMITTED:{sessionId}");
        q.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.Now.ToString("O"));

        if (await q.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Restaurant-TSE-Vorgang konnte nicht atomar als verarbeitet markiert werden.");
        }
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
