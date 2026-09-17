using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R135: training sales of a till that books for real, recorded as AVTraining.
///
/// AEAO zu § 146a Nr. 1.11.1 lists Trainingsbuchungen among the Vorgänge to be
/// secured; DSFinV-K 4.2.6: "In vielen Prüfungsfällen wurde festgestellt, dass
/// ... Trainingsbediener genutzt wurde, um steuerpflichtige Bareinnahmen nicht
/// zu erfassen. Aus diesem Grund sind diese Buchungen auch zu protokollieren und
/// abzusichern" - without effect on the closing (Anhang B).
///
/// They live in their own immutable tables, never in <c>sales</c>, so no
/// report, stock booking or Z total can ever count them.
/// </summary>
public sealed class TrainingReceiptRepository
{
    private readonly SqliteDatabase _db;

    public TrainingReceiptRepository(SqliteDatabase db) => _db = db;

    public Task<Sale> RecordAsync(CheckoutSnapshot snapshot, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            // Only a till that books for real records training fiscally.
            FiscalRelease.RequireProduction();

            var now = DateTimeOffset.Now;
            await using var c = _db.OpenConnection();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

            long number;
            await using (var seq = c.CreateCommand())
            {
                seq.Transaction = tx;
                seq.CommandText = """
                    INSERT OR IGNORE INTO app_sequence(key,value) VALUES('training_receipt',0);
                    UPDATE app_sequence SET value=value+1 WHERE key='training_receipt';
                    SELECT value FROM app_sequence WHERE key='training_receipt';
                    """;
                number = Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
            }

            long id;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO training_receipts(
                      training_number,created_at,operator_name,payment_method,
                      discount_cents,total_cents,cash_portion_cents,card_portion_cents,im_haus,started_at)
                    VALUES($n,$at,$op,$pm,$disc,$total,$cash,$card,$imHaus,$started);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$n", number);
                q.Parameters.AddWithValue("$at", now.ToString("O"));
                q.Parameters.AddWithValue("$op", snapshot.OperatorName ?? "");
                q.Parameters.AddWithValue("$pm", snapshot.Method.ToString().ToUpperInvariant());
                q.Parameters.AddWithValue("$disc", snapshot.DiscountCents);
                q.Parameters.AddWithValue("$total", snapshot.TotalCents);
                q.Parameters.AddWithValue("$cash", snapshot.EffectiveCashPortionCents);
                q.Parameters.AddWithValue("$card", snapshot.EffectiveCardPortionCents);
                q.Parameters.AddWithValue("$imHaus", snapshot.ImHaus ? 1 : 0);
                q.Parameters.AddWithValue("$started", (snapshot.StartedAt ?? now).ToString("O"));
                id = Convert.ToInt64(await q.ExecuteScalarAsync(ct));
            }

            foreach (var line in snapshot.Lines)
                await InsertLineAsync(c, tx, id, line, ct);
            // R143: positions cancelled during capture belong to the training record too.
            await CancelledPositionStore.InsertAsync(c, tx, CancelledPositionStore.Trainings, id, snapshot.CancelledLines, ct);
            // R147: the training order it paid.
            await LinkOrderAsync(c, tx, id, snapshot.ParkedReceiptId, ct);

            await tx.CommitAsync(ct);
            return (await LoadAsync(c, id, ct))!.Value.Sale;
        });

    public Task RecordTseResultAsync(long trainingId, SaleTseResult result, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using (var existing = c.CreateCommand())
            {
                existing.CommandText = "SELECT COUNT(*) FROM training_tse_signatures WHERE training_id=$id;";
                existing.Parameters.AddWithValue("$id", trainingId);
                if (Convert.ToInt64(await existing.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException(
                        $"Für Trainingsvorgang {trainingId} existiert bereits ein endgültiger TSE-Eintrag. Ein nachträgliches Signieren ist nicht vorgesehen (R129).");
            }

            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO training_tse_signatures(
                  training_id,client_id,transaction_number,signature_counter,serial_number,
                  signature,log_time,outage,outage_reason,created_at,start_log_time)
                VALUES($id,$client,$tanr,$sigz,$serial,$sig,$log,$outage,$reason,$at,$startlog);
                """;
            q.Parameters.AddWithValue("$id", trainingId);
            q.Parameters.AddWithValue("$startlog", result.StartLogTime?.ToString("O") ?? "");
            q.Parameters.AddWithValue("$client", result.ClientId);
            q.Parameters.AddWithValue("$tanr", result.TransactionNumber);
            q.Parameters.AddWithValue("$sigz", result.SignatureCounter);
            q.Parameters.AddWithValue("$serial", result.SerialNumber);
            q.Parameters.AddWithValue("$sig", result.Signature);
            q.Parameters.AddWithValue("$log", result.LogTime?.ToString("O") ?? "");
            q.Parameters.AddWithValue("$outage", result.Signed ? 0 : 1);
            q.Parameters.AddWithValue("$reason", result.OutageMessage);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync(ct);
        });

    /// <summary>
    /// R147: DSFinV-K 2.7.1 - which training order a training receipt paid, so the
    /// export can give both the same Abrechnungskreis. A real receipt is linked
    /// through parked_receipts.cashed_sale_id; a training order is only marked
    /// SIMULATED and had no link at all.
    /// </summary>
    public static async Task LinkOrderAsync(SqliteConnection c, SqliteTransaction tx, long trainingId, long? parkedReceiptId, CancellationToken ct = default)
    {
        if (parkedReceiptId is not long orderId)
            return;

        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO training_receipt_orders(training_id,parked_receipt_id) VALUES($t,$p);";
        q.Parameters.AddWithValue("$t", trainingId);
        q.Parameters.AddWithValue("$p", orderId);
        await q.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// R147: training receipt id → Abrechnungskreis of the secured training order
    /// it paid (the same name the order records carry).
    /// </summary>
    internal static async Task<Dictionary<long, string>> LoadAllocationGroupsAsync(SqliteConnection c, CancellationToken ct)
    {
        var groups = new Dictionary<long, string>();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT l.training_id,p.park_number FROM training_receipt_orders l
            JOIN parked_receipts p ON p.id=l.parked_receipt_id
            WHERE p.tse_transaction_number<>'' OR p.tse_outage=1
               OR EXISTS (SELECT 1 FROM order_bestellungen b WHERE b.parked_receipt_id=p.id);
            """;
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            groups[r.GetInt64(0)] = DsfinvkClosingBuilder.OrderAllocationGroup(new ParkedReceipt { ParkNumber = r.GetInt64(1) });
        return groups;
    }

    /// <summary>Training sales in a closing window (UTC text bounds as the Z-Bericht), with their TSE records.</summary>
    internal static async Task<List<DsfinvkTraining>> LoadInPeriodAsync(SqliteConnection c, string fromUtc, string toUtc, CancellationToken ct)
    {
        var ids = new List<long>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id FROM training_receipts WHERE created_at_utc > $from AND created_at_utc <= $to ORDER BY training_number;";
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ids.Add(r.GetInt64(0));
        }

        var trainings = new List<DsfinvkTraining>();
        foreach (var id in ids)
        {
            var loaded = await LoadAsync(c, id, ct);
            if (loaded is { } value)
                trainings.Add(new DsfinvkTraining(value.Sale, value.Tse));
        }

        return trainings;
    }

    private static async Task<(Sale Sale, DsfinvkTseResult? Tse)?> LoadAsync(SqliteConnection c, long id, CancellationToken ct)
    {
        Sale sale;
        DsfinvkTseResult? tse = null;
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT r.training_number,r.created_at,r.operator_name,r.payment_method,r.discount_cents,r.total_cents,
                       r.cash_portion_cents,r.card_portion_cents,r.im_haus,
                       t.training_id,t.client_id,t.transaction_number,t.signature_counter,t.serial_number,t.signature,
                       t.log_time,t.outage,t.outage_reason,r.started_at,t.start_log_time
                FROM training_receipts r
                LEFT JOIN training_tse_signatures t ON t.training_id=r.id
                WHERE r.id=$id;
                """;
            q.Parameters.AddWithValue("$id", id);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            sale = new Sale
            {
                Id = id,
                ReceiptNumber = r.GetInt64(0),
                CreatedAt = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                OperatorName = r.GetString(2),
                PaymentMethod = r.GetString(3) switch { "CARD" => PaymentMethod.Card, "MIXED" => PaymentMethod.Mixed, _ => PaymentMethod.Cash },
                DiscountCents = r.GetInt64(4),
                TotalCents = r.GetInt64(5),
                CashPortionCents = r.GetInt64(6),
                CardPortionCents = r.GetInt64(7),
                ImHaus = r.GetInt64(8) != 0,
                TransactionType = FiscalProcessData.TrainingTransactionType,
                FiscalStatus = "TRAINING",
                StartedAt = r.IsDBNull(18) ? null : DateTimeOffset.Parse(r.GetString(18), CultureInfo.InvariantCulture),
            };

            if (!r.IsDBNull(9))
            {
                var logTime = string.IsNullOrWhiteSpace(r.GetString(15)) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture);
                var startLogTime = string.IsNullOrWhiteSpace(r.GetString(19)) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(19), CultureInfo.InvariantCulture);
                tse = new DsfinvkTseResult(r.GetString(13), r.GetString(11), r.GetString(12), r.GetString(14), logTime, r.GetInt64(16) != 0, r.GetString(17), startLogTime);
                sale.TseStartLogTime = startLogTime;
                sale.TseClientId = r.GetString(10);
                sale.TseTransactionNumber = r.GetString(11);
                sale.TseSignatureCounter = r.GetString(12);
                sale.TseSerialNumber = r.GetString(13);
                sale.TseSignature = r.GetString(14);
                sale.TseLogTime = logTime;
                sale.TseOutage = r.GetInt64(16) != 0;
            }
        }

        var lines = new List<CartLine>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                       list_unit_price_cents,promotion_id,promotion_name,promotion_percent,promotion_discount_unit_cents
                FROM training_receipt_items WHERE training_id=$id ORDER BY id;
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
                    ListUnitPriceCents = r.GetInt64(8) > 0 ? r.GetInt64(8) : r.GetInt64(5) + r.GetInt64(12),
                    PromotionId = r.GetInt64(9),
                    PromotionName = r.GetString(10),
                    PromotionPercent = r.GetInt32(11),
                    PromotionDiscountUnitCents = r.GetInt64(12),
                });
            }
        }

        sale.Lines = lines;
        sale.CancelledLines = await CancelledPositionStore.LoadAsync(c, CancelledPositionStore.Trainings, id, ct);
        return (sale, tse);
    }

    private static async Task InsertLineAsync(SqliteConnection c, SqliteTransaction tx, long trainingId, CartLine line, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT INTO training_receipt_items(
              training_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,
              pfand_cents,line_total_cents,list_unit_price_cents,promotion_id,promotion_name,promotion_percent,
              promotion_discount_unit_cents)
            VALUES($t,$p,$n,$v,$b,$q,$u,$vat,$pfand,$total,$list,$pid,$pname,$ppct,$punit);
            """;
        q.Parameters.AddWithValue("$t", trainingId);
        q.Parameters.AddWithValue("$p", line.ProductId);
        q.Parameters.AddWithValue("$n", line.ProductName);
        q.Parameters.AddWithValue("$v", line.VariantName);
        q.Parameters.AddWithValue("$b", line.Barcode);
        q.Parameters.AddWithValue("$q", (double)line.Quantity);
        q.Parameters.AddWithValue("$u", line.UnitPriceCents);
        q.Parameters.AddWithValue("$vat", (double)line.VatRate);
        q.Parameters.AddWithValue("$pfand", line.PfandCents);
        q.Parameters.AddWithValue("$total", line.LineTotalCents);
        q.Parameters.AddWithValue("$list", line.EffectiveListUnitPriceCents);
        q.Parameters.AddWithValue("$pid", line.PromotionId);
        q.Parameters.AddWithValue("$pname", line.PromotionName);
        q.Parameters.AddWithValue("$ppct", line.PromotionPercent);
        q.Parameters.AddWithValue("$punit", line.PromotionDiscountUnitCents);
        await q.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>R135: TSE signing of a training sale as AVTraining (Kassenbeleg-V1).</summary>
public sealed class TrainingFiscalSigningService
{
    private readonly TseFailSafeService _tse;
    private readonly ISettingsRepository _settings;
    private readonly TrainingReceiptRepository _trainings;

    public TrainingFiscalSigningService(TseFailSafeService tse, ISettingsRepository settings, TrainingReceiptRepository trainings)
    {
        _tse = tse;
        _settings = settings;
        _trainings = trainings;
    }

    /// <summary>R136: see SaleFiscalSigningService.Vorgaenge.</summary>
    public TseVorgangService? Vorgaenge { get; init; }

    /// <summary>R136: ends the training Vorgang started with the first position of the cart.</summary>
    public async Task<SaleTseResult> SignInVorgangAsync(Sale training, string vorgangId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(training);
        if (string.IsNullOrEmpty(vorgangId) || Vorgaenge is not { } vorgaenge)
            return await SignAsync(training, actor, ct);
        if (training.TransactionType != FiscalProcessData.TrainingTransactionType)
            throw new InvalidOperationException("Nur Trainingsvorgänge werden hier als AVTraining abgesichert.");

        var reference = $"TRAINING:{training.Id}";
        string processData;
        try
        {
            processData = FiscalProcessData.KassenbelegText(training);
        }
        catch (UnsupportedVatRateException ex)
        {
            await vorgaenge.CloseUnsignedAsync(vorgangId, reference, ct);
            await _tse.ReportUnavailableAsync(ex.Message, actor, ct);
            var refused = SaleTseResult.Outage(ex.Message);
            await _trainings.RecordTseResultAsync(training.Id, refused, ct);
            return refused;
        }

        var result = await vorgaenge.FinishAsync(vorgangId, FiscalProcessData.KassenbelegProcessType, processData, actor, reference, ct);
        await _trainings.RecordTseResultAsync(training.Id, result, ct);
        return result;
    }

    public async Task<SaleTseResult> SignAsync(Sale training, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(training);
        if (training.TransactionType != FiscalProcessData.TrainingTransactionType)
            throw new InvalidOperationException("Nur Trainingsvorgänge werden hier als AVTraining abgesichert.");

        // A rate without an Anhang I container cannot be signed correctly;
        // the training is recorded as an outage instead (as for sales, R130).
        string processData;
        try
        {
            processData = FiscalProcessData.KassenbelegText(training);
        }
        catch (UnsupportedVatRateException ex)
        {
            await _tse.ReportUnavailableAsync(ex.Message, actor, ct);
            var refused = SaleTseResult.Outage(ex.Message);
            await _trainings.RecordTseResultAsync(training.Id, refused, ct);
            return refused;
        }

        var result = await TseKassenbelegSigner.SignAsync(_tse, _settings, processData, "Trainingsvorgang", actor, ct);
        await _trainings.RecordTseResultAsync(training.Id, result, ct);
        return result;
    }
}
