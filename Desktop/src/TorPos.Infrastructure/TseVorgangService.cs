using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record TseVorgangRecord(
    string Id,
    bool Training,
    DateTimeOffset StartedAt,
    string ClientId,
    string TransactionNumber,
    DateTimeOffset? StartLogTime,
    string StartError,
    string State,
    long? ParkedReceiptId);

/// <summary>
/// R136: the TSE transaction of a Vorgang is started when the Vorgang begins -
/// the first position in the cart - and finished when it ends.
///
/// AEAO zu § 146a Nr. 2.2.2: "Das Aufzeichnungssystem muss unmittelbar mit
/// Beginn eines aufzuzeichnenden Vorgangs die Protokollierung des Vorgangs in
/// der TSE starten". Nr. 2.2.3.3: the Vorgang is ended before its receipt is
/// issued, and no Vorgang may stay open at a closing. Until R136 TOR started
/// and finished the transaction together after payment, so the TSE's
/// Vorgangsbeginn was really the end of the sale.
///
/// <c>tse_vorgaenge</c> holds the open transactions across a restart; it is
/// operational state. The fiscal records stay where they were
/// (sale_tse_signatures, ...), now with the start log time, and a Vorgang that
/// ends without a receipt is kept as an immutable AVBelegabbruch record
/// (AEAO Nr. 1.11.1 "Belegabbrüche").
///
/// A TSE failure is a documented outage, never an exception that could stop the
/// cashier: the Vorgang itself is never refused.
/// </summary>
public sealed class TseVorgangService
{
    public const string Open = "OPEN";
    public const string Parked = "PARKED";
    public const string Finished = "FINISHED";
    public const string Aborted = "ABORTED";

    private readonly SqliteDatabase _db;
    private readonly TseFailSafeService _tse;
    private readonly ISettingsRepository _settings;

    public TseVorgangService(SqliteDatabase db, TseFailSafeService tse, ISettingsRepository settings)
    {
        _db = db;
        _tse = tse;
        _settings = settings;
    }

    // ------------------------------------------------------------------ start

    /// <summary>Starts the TSE transaction of a new Vorgang. Idempotent per Vorgang id.</summary>
    public async Task StartAsync(string vorgangId, bool training, DateTimeOffset startedAt, string actor, CancellationToken ct = default)
    {
        if (await GetAsync(vorgangId, ct) is not null)
            return;

        await ExecuteAsync("""
            INSERT INTO tse_vorgaenge(id,training,started_at,state,updated_at)
            VALUES($id,$training,$started,'OPEN',$now);
            """, q =>
        {
            q.Parameters.AddWithValue("$id", vorgangId);
            q.Parameters.AddWithValue("$training", training ? 1 : 0);
            q.Parameters.AddWithValue("$started", startedAt.ToString("O"));
        }, ct);

        var (clientId, problem) = await TseReadyAsync(actor, ct);
        if (problem is not null)
        {
            await SetStartAsync(vorgangId, "", "", null, problem, ct);
            return;
        }

        var (start, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
            actor,
            ct);

        if (start.Success)
            await SetStartAsync(vorgangId, clientId, start.TransactionNumber.ToString(CultureInfo.InvariantCulture), start.LogTime, "", ct);
        else
            await SetStartAsync(vorgangId, "", "", null, start.Message, ct);
    }

    // ----------------------------------------------------------------- finish

    /// <summary>
    /// Ends the Vorgang with its data and returns the TSE result. A transaction
    /// that was started at Vorgangsbeginn is finished as the SAME transaction.
    ///
    /// KassenSichV §2 requires the transaction to start immediately with the
    /// Vorgang. If that start failed, TOR preserves the Vorgang as an outage;
    /// it never creates a later payment-time transaction and presents that
    /// later start time as the original one.
    /// </summary>
    public async Task<SaleTseResult> FinishAsync(string vorgangId, string processType, string processData, string actor, string reference, CancellationToken ct = default)
    {
        var vorgang = await GetAsync(vorgangId, ct);
        SaleTseResult result;

        if (vorgang is { State: Open or Parked } started)
        {
            if (TryTransaction(started, out var transaction))
            {
                var (finish, _) = await _tse.FinishTransactionAsync(
                    new TseTransactionFinishRequest(started.ClientId, transaction, System.Text.Encoding.UTF8.GetBytes(processData), processType),
                    actor,
                    ct);

                result = finish.Success
                    ? SaleTseResult.FromSuccessfulTse(
                        started.ClientId,
                        finish.TransactionNumber.ToString(CultureInfo.InvariantCulture),
                        finish.SignatureCounter.ToString(CultureInfo.InvariantCulture),
                        finish.SerialNumber,
                        finish.SignatureBase64,
                        finish.LogTime,
                        started.StartLogTime)
                    : SaleTseResult.Outage(finish.Message);

                if (finish.Success && !result.Signed)
                    await _tse.ReportUnavailableAsync(result.OutageMessage, actor, ct);
            }
            else
            {
                result = SaleTseResult.Outage(
                    started.StartError.Length > 0
                        ? started.StartError
                        : "TSE-Transaktion wurde bei Vorgangsbeginn nicht gestartet.");
            }
        }
        else
        {
            result = await StartAndFinishAsync(processType, processData, actor, ct);
        }

        if (vorgang is not null)
            await SetStateAsync(vorgangId, Finished, reference, ct);
        return result;
    }

    /// <summary>
    /// The Vorgang became a record whose data cannot be signed (a VAT rate
    /// without an Anhang I tax container). It is closed here without a TSE
    /// result; the record itself carries the outage.
    /// </summary>
    public Task CloseUnsignedAsync(string vorgangId, string reference, CancellationToken ct = default) =>
        SetStateAsync(vorgangId, Finished, reference, ct);

    // ------------------------------------------------------------ park/resume

    /// <summary>
    /// A parked receipt keeps its Vorgang - and the TSE transaction - open.
    /// R138: the till no longer does this; parking secures the receipt as an
    /// order (Bestellung-V1). Kept for Vorgänge parked under R136.
    /// </summary>
    public Task ParkAsync(string vorgangId, long parkedReceiptId, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE tse_vorgaenge SET state='PARKED',parked_receipt_id=$parked,updated_at=$now WHERE id=$id AND state IN ('OPEN','PARKED');", q =>
        {
            q.Parameters.AddWithValue("$id", vorgangId);
            q.Parameters.AddWithValue("$parked", parkedReceiptId);
        }, ct);

    /// <summary>The Vorgang of a parked receipt that is taken up again, if it has one.</summary>
    public async Task<TseVorgangRecord?> ResumeParkedAsync(long parkedReceiptId, CancellationToken ct = default)
    {
        var id = await ParkedVorgangIdAsync(parkedReceiptId, ct);
        if (id is null)
            return null;

        await ExecuteAsync("UPDATE tse_vorgaenge SET state='OPEN',updated_at=$now WHERE id=$id;", q => q.Parameters.AddWithValue("$id", id), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>A parked receipt is deleted: its Vorgang ends as aborted with the parked positions.</summary>
    public async Task AbortParkedAsync(long parkedReceiptId, IReadOnlyList<CartLine> lines, long discountCents, string operatorName, string actor, CancellationToken ct = default)
    {
        if (await ParkedVorgangIdAsync(parkedReceiptId, ct) is { } id)
            await AbortAsync(id, lines, discountCents, operatorName, actor, ct);
    }

    // ------------------------------------------------------------------ abort

    /// <summary>
    /// Ends a Vorgang that did not become a receipt: the TSE transaction is
    /// finished with the Anhang I abort data, and the positions that were in the
    /// cart are kept as an immutable AVBelegabbruch record.
    /// </summary>
    public async Task AbortAsync(string vorgangId, IReadOnlyList<CartLine> lines, long discountCents, string operatorName, string actor, CancellationToken ct = default)
    {
        var vorgang = await GetAsync(vorgangId, ct);
        if (vorgang is null || vorgang.State is Finished or Aborted)
            return;

        SaleTseResult result;
        if (TryTransaction(vorgang, out var transaction))
        {
            var (finish, _) = await _tse.FinishTransactionAsync(
                new TseTransactionFinishRequest(vorgang.ClientId, transaction, System.Text.Encoding.UTF8.GetBytes(FiscalProcessData.AbortText(vorgang.Training)), FiscalProcessData.KassenbelegProcessType),
                actor,
                ct);
            result = finish.Success
                ? SaleTseResult.FromSuccessfulTse(
                    vorgang.ClientId,
                    finish.TransactionNumber.ToString(CultureInfo.InvariantCulture),
                    finish.SignatureCounter.ToString(CultureInfo.InvariantCulture),
                    finish.SerialNumber,
                    finish.SignatureBase64,
                    finish.LogTime,
                    vorgang.StartLogTime)
                : SaleTseResult.Outage(finish.Message);
        }
        else
        {
            result = SaleTseResult.Outage(vorgang.StartError.Length > 0 ? vorgang.StartError : "TSE-Transaktion wurde nicht gestartet.");
        }

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
            long id;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO aborted_vorgaenge(
                      vorgang_id,training,started_at,ended_at,operator_name,discount_cents,total_cents,
                      client_id,transaction_number,signature_counter,serial_number,signature,
                      start_log_time,log_time,outage,outage_reason)
                    VALUES($vorgang,$training,$started,$ended,$op,$discount,$total,
                      $client,$tanr,$sigz,$serial,$sig,$startLog,$log,$outage,$reason);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$vorgang", vorgangId);
                q.Parameters.AddWithValue("$training", vorgang.Training ? 1 : 0);
                q.Parameters.AddWithValue("$started", vorgang.StartedAt.ToString("O"));
                q.Parameters.AddWithValue("$ended", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$op", operatorName ?? "");
                q.Parameters.AddWithValue("$discount", discountCents);
                q.Parameters.AddWithValue("$total", Math.Max(0, lines.Sum(l => l.LineTotalCents) - discountCents));
                q.Parameters.AddWithValue("$client", result.ClientId);
                q.Parameters.AddWithValue("$tanr", result.TransactionNumber);
                q.Parameters.AddWithValue("$sigz", result.SignatureCounter);
                q.Parameters.AddWithValue("$serial", result.SerialNumber);
                q.Parameters.AddWithValue("$sig", result.Signature);
                q.Parameters.AddWithValue("$startLog", result.StartLogTime?.ToString("O") ?? "");
                q.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
                q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
                q.Parameters.AddWithValue("$reason", result.OutageMessage);
                id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
            }

            foreach (var line in lines)
            {
                await using var item = c.CreateCommand();
                item.Transaction = tx;
                item.CommandText = """
                    INSERT INTO aborted_vorgang_items(
                      aborted_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,
                      pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,
                      promotion_discount_unit_cents)
                    VALUES($a,$p,$n,$v,$b,$q,$u,$vat,$pfand,$total,$list,$pid,$pname,$ppct,$punit);
                    """;
                item.Parameters.AddWithValue("$a", id);
                item.Parameters.AddWithValue("$p", line.ProductId);
                item.Parameters.AddWithValue("$n", line.ProductName);
                item.Parameters.AddWithValue("$v", line.VariantName);
                item.Parameters.AddWithValue("$b", line.Barcode);
                item.Parameters.AddWithValue("$q", (double)line.Quantity);
                item.Parameters.AddWithValue("$u", line.UnitPriceCents);
                item.Parameters.AddWithValue("$vat", (double)line.VatRate);
                item.Parameters.AddWithValue("$pfand", line.PfandCents);
                item.Parameters.AddWithValue("$total", line.LineTotalCents);
                item.Parameters.AddWithValue("$list", line.EffectiveListUnitPriceCents);
                item.Parameters.AddWithValue("$pid", line.PromotionId);
                item.Parameters.AddWithValue("$pname", line.PromotionName);
                item.Parameters.AddWithValue("$ppct", line.PromotionPercent);
                item.Parameters.AddWithValue("$punit", line.PromotionDiscountUnitCents);
                await item.ExecuteNonQueryAsync(ct);
            }

            await using (var state = c.CreateCommand())
            {
                state.Transaction = tx;
                state.CommandText = "UPDATE tse_vorgaenge SET state='ABORTED',reference=$ref,updated_at=$now WHERE id=$id;";
                state.Parameters.AddWithValue("$ref", "ABORT:" + id.ToString(CultureInfo.InvariantCulture));
                state.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                state.Parameters.AddWithValue("$id", vorgangId);
                await state.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// At start-up and before a closing: every Vorgang still OPEN whose cart is
    /// not at the till (a crash without a recoverable cart, a training cart), and
    /// every PARKED one whose receipt is no longer parked, is ended as aborted.
    /// </summary>
    public async Task<int> AbortOrphansAsync(string? keepVorgangId, string actor, CancellationToken ct = default)
    {
        var orphans = await QueryAsync("""
            SELECT v.id
            FROM tse_vorgaenge v
            LEFT JOIN parked_receipts p ON p.id=v.parked_receipt_id
            WHERE v.state='OPEN'
               OR (v.state='PARKED' AND (p.id IS NULL OR p.status<>'OPEN'))
            ORDER BY v.started_at;
            """, _ => { }, r => r.GetString(0), ct);

        var count = 0;
        foreach (var id in orphans.Where(id => id != keepVorgangId))
        {
            await AbortAsync(id, Array.Empty<CartLine>(), 0, "SYSTEM", actor, ct);
            count++;
        }

        return count;
    }

    // ---------------------------------------------------------------- queries

    public async Task<TseVorgangRecord?> GetAsync(string vorgangId, CancellationToken ct = default)
    {
        var rows = await QueryAsync("""
            SELECT id,training,started_at,client_id,transaction_number,start_log_time,start_error,state,parked_receipt_id
            FROM tse_vorgaenge WHERE id=$id;
            """, q => q.Parameters.AddWithValue("$id", vorgangId), r => new TseVorgangRecord(
                r.GetString(0),
                r.GetInt64(1) != 0,
                DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                r.GetString(3),
                r.GetString(4),
                string.IsNullOrWhiteSpace(r.GetString(5)) ? null : DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
                r.GetString(6),
                r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt64(8)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// R138: Vorgänge still OPEN although their sale was booked - the till
    /// stopped between the sale commit and the end of the TSE transaction.
    /// <c>Signed</c>: the sale already has its final TSE record.
    /// </summary>
    public Task<List<(string VorgangId, long SaleId, bool Signed)>> CommittedSalesWithOpenVorgangAsync(CancellationToken ct = default) =>
        QueryAsync("""
            SELECT v.id, o.sale_id,
                   EXISTS (SELECT 1 FROM sale_tse_signatures t WHERE t.sale_id=o.sale_id)
            FROM tse_vorgaenge v
            JOIN checkout_operations o ON json_extract(o.snapshot,'$.TseVorgangId')=v.id
            WHERE v.state='OPEN' AND o.sale_id IS NOT NULL
            ORDER BY v.started_at;
            """, _ => { }, r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2) != 0), ct);

    /// <summary>R136: when the Vorgang of an accepted order began (BON_START of the AVBestellung).</summary>
    public Task RecordOrderStartAsync(long parkedReceiptId, DateTimeOffset startedAt, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE parked_receipts SET vorgang_started_at=$started WHERE id=$id;", q =>
        {
            q.Parameters.AddWithValue("$id", parkedReceiptId);
            q.Parameters.AddWithValue("$started", startedAt.ToString("O"));
        }, ct);

    /// <summary>Aborted Vorgänge in a closing window, for the DSFinV-K export.</summary>
    internal static async Task<List<DsfinvkAbortedVorgang>> LoadAbortedInPeriodAsync(SqliteConnection c, string fromUtc, string toUtc, CancellationToken ct)
    {
        var heads = new List<(long Id, DsfinvkAbortedVorgang Vorgang)>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,started_at,ended_at,operator_name,discount_cents,training,
                       serial_number,transaction_number,signature_counter,signature,log_time,outage,outage_reason,start_log_time
                FROM aborted_vorgaenge
                WHERE ended_at_utc > $from AND ended_at_utc <= $to
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                DateTimeOffset? Time(int i) => string.IsNullOrWhiteSpace(r.GetString(i)) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture);
                var tse = new DsfinvkTseResult(r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), Time(10), r.GetInt64(11) != 0, r.GetString(12), Time(13));
                heads.Add((r.GetInt64(0), new DsfinvkAbortedVorgang(
                    r.GetInt64(0),
                    DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture),
                    r.GetString(3),
                    Array.Empty<CartLine>(),
                    r.GetInt64(4),
                    tse,
                    r.GetInt64(5) != 0)));
            }
        }

        var result = new List<DsfinvkAbortedVorgang>();
        foreach (var (id, vorgang) in heads)
        {
            var lines = new List<CartLine>();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                       list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents
                FROM aborted_vorgang_items WHERE aborted_id=$id ORDER BY id;
                """;
            q.Parameters.AddWithValue("$id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add(new CartLine
                {
                    ProductId = r.GetInt64(0),
                    ProductName = r.GetString(1),
                    VariantName = r.GetString(2),
                    Barcode = r.GetString(3),
                    Quantity = Convert.ToDecimal(r.GetDouble(4)),
                    UnitPriceCents = r.GetInt64(5),
                    VatRate = Convert.ToDecimal(r.GetDouble(6)),
                    PfandCents = r.GetInt64(7),
                    ListUnitPriceCents = r.GetInt64(8),
                    PromotionId = r.GetInt64(9),
                    PromotionName = r.GetString(10),
                    PromotionPercent = r.GetInt32(11),
                    PromotionDiscountUnitCents = r.GetInt64(12),
                });
            }

            result.Add(vorgang with { Lines = lines });
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static bool TryTransaction(TseVorgangRecord vorgang, out ulong transaction)
    {
        transaction = 0;
        return vorgang.ClientId.Length > 0 &&
            ulong.TryParse(vorgang.TransactionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out transaction);
    }

    private async Task<string?> ParkedVorgangIdAsync(long parkedReceiptId, CancellationToken ct)
    {
        var found = await QueryAsync(
            "SELECT id FROM tse_vorgaenge WHERE parked_receipt_id=$parked AND state='PARKED' ORDER BY started_at DESC LIMIT 1;",
            q => q.Parameters.AddWithValue("$parked", parkedReceiptId),
            r => r.GetString(0),
            ct);
        return found.Count == 0 ? null : found[0];
    }

    private async Task<SaleTseResult> StartAndFinishAsync(string processType, string processData, string actor, CancellationToken ct)
    {
        var (clientId, problem) = await TseReadyAsync(actor, ct);
        if (problem is not null)
            return SaleTseResult.Outage(problem);

        var (start, _) = await _tse.StartTransactionAsync(
            new TseTransactionStartRequest(clientId, FiscalProcessData.StartProcessData, FiscalProcessData.StartProcessType),
            actor,
            ct);
        if (!start.Success)
            return SaleTseResult.Outage(start.Message);

        var (finish, _) = await _tse.FinishTransactionAsync(
            new TseTransactionFinishRequest(clientId, start.TransactionNumber, System.Text.Encoding.UTF8.GetBytes(processData), processType),
            actor,
            ct);
        if (!finish.Success)
            return SaleTseResult.Outage(finish.Message);

        var result = SaleTseResult.FromSuccessfulTse(
            clientId,
            finish.TransactionNumber.ToString(CultureInfo.InvariantCulture),
            finish.SignatureCounter.ToString(CultureInfo.InvariantCulture),
            finish.SerialNumber,
            finish.SignatureBase64,
            finish.LogTime,
            start.LogTime);

        if (!result.Signed)
            await _tse.ReportUnavailableAsync(result.OutageMessage, actor, ct);

        return result;
    }

    private async Task<(string ClientId, string? Problem)> TseReadyAsync(string actor, CancellationToken ct)
    {
        var settings = await _settings.LoadAllAsync(ct);
        var status = (settings.GetValueOrDefault("tse.status") ?? "").Trim();
        if (!string.Equals(status, "AKTIV", StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"TSE ist nicht aktiv (Status: {(status.Length == 0 ? "nicht gesetzt" : status)}). Vorgang ohne TSE-Signatur.";
            await _tse.ReportUnavailableAsync(reason, actor, ct);
            return ("", reason);
        }

        var clientId = (settings.GetValueOrDefault("tse.client_id") ?? "").Trim();
        if (clientId.Length == 0)
        {
            const string reason = "TSE-Client-ID ist nicht konfiguriert. Vorgang ohne TSE-Signatur.";
            await _tse.ReportUnavailableAsync(reason, actor, ct);
            return ("", reason);
        }

        return (clientId, null);
    }

    private Task SetStartAsync(string id, string clientId, string transaction, DateTimeOffset? logTime, string error, CancellationToken ct) =>
        ExecuteAsync("UPDATE tse_vorgaenge SET client_id=$client,transaction_number=$tanr,start_log_time=$log,start_error=$error,updated_at=$now WHERE id=$id;", q =>
        {
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$client", clientId);
            q.Parameters.AddWithValue("$tanr", transaction);
            q.Parameters.AddWithValue("$log", logTime?.ToString("O") ?? "");
            q.Parameters.AddWithValue("$error", error);
        }, ct);

    private Task SetStateAsync(string id, string state, string reference, CancellationToken ct) =>
        ExecuteAsync("UPDATE tse_vorgaenge SET state=$state,reference=$ref,updated_at=$now WHERE id=$id;", q =>
        {
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$state", state);
            q.Parameters.AddWithValue("$ref", reference);
        }, ct);

    private Task ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = sql;
            bind(q);
            if (sql.Contains("$now", StringComparison.Ordinal))
                q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync(ct);
        });

    private Task<List<T>> QueryAsync<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> map, CancellationToken ct) =>
        IoQueue.RunAsync(async () =>
        {
            var rows = new List<T>();
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = sql;
            bind(q);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(map(r));
            return rows;
        });
}
