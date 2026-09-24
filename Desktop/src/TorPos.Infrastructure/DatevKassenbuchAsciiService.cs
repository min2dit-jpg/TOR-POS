using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record DatevKassenbuchAsciiExport(
    long Id,
    long ZArchiveId,
    long ZNumber,
    DateTimeOffset CreatedAt,
    string CsvPath,
    string CsvSha256,
    string PdfPath,
    string EmailState,
    string EmailRecipient,
    int EmailAttempts,
    DateTimeOffset? EmailSentAt,
    string LastError);

public sealed record DatevKassenbuchAsciiRow(
    string Currency,
    long SignedAmountCents,
    string ReceiptNumber,
    DateOnly BookingDate,
    string BookingText,
    decimal? VatRate,
    string BookingKey,
    string CounterAccount,
    string Cost1,
    string Cost2,
    string Quantity,
    string Discount,
    string Message);

/// <summary>
/// R155 - DATEV Kassenbuch Standard-ASCII file path without a DATEV API integration.
///
/// DATEV Kassenbuch online accepts Kassenbewegungen in its Standard-ASCII
/// format (13 columns, semicolon separated). No DATEV API membership or
/// Developer Portal credential is needed to CREATE this file. The operator
/// can import it manually or TOR can e-mail the immutable CSV + Z PDF to the
/// saved Steuerberater address using the already configured SMTP/Google
/// transport.
///
/// The export intentionally contains cash-book movements only. Card-only
/// turnover is not a physical cash movement and therefore does not belong in
/// a Kassenbuch import file; it remains documented in Z/DSFinV-K reports.
/// </summary>
public sealed class DatevKassenbuchAsciiService
{
    public const string SettingEnabled = "datev.ascii.enabled";
    public const string SettingAutoAfterZ = "datev.ascii.auto_after_z";
    public const string SettingAutoEmail = "datev.ascii.auto_email";
    public const string SettingRecipient = "datev.ascii.recipient";

    public const string Header =
        "Währung;VorzBetrag;RechNr;BelegDatum;Belegtext;UStSatz;BU;Gegenkonto;Kost1;Kost2;Kostmenge;Skonto;Nachricht";

    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;
    private readonly BusinessManagementService _management;
    private readonly ReportEmailService _email;
    private readonly IAuditLog _audit;

    public DatevKassenbuchAsciiService(
        SqliteDatabase db,
        ISettingsRepository settings,
        BusinessManagementService management,
        ReportEmailService email,
        IAuditLog audit)
    {
        _db = db;
        _settings = settings;
        _management = management;
        _email = email;
        _audit = audit;
    }

    public string ExportDirectory =>
        Path.Combine(AppPaths.DataDirectory, "DATEV", "Kassenbuch");

    public async Task<DatevKassenbuchAsciiExport> PrepareAsync(
        ZArchiveRow z,
        string actor,
        CancellationToken ct = default)
    {
        var existing = await FindByZAsync(z.Id, ct);
        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.CsvPath) &&
            File.Exists(existing.CsvPath) &&
            !string.IsNullOrWhiteSpace(existing.CsvSha256))
        {
            VerifyHash(existing.CsvPath, existing.CsvSha256);
            return existing;
        }

        var rows = await BuildRowsAsync(z, ct);
        if (rows.Count == 0)
            throw new InvalidOperationException(
                "Für diesen Z-Abschluss wurden keine DATEV-Kassenbuch-Bewegungen gefunden.");

        Directory.CreateDirectory(ExportDirectory);
        var baseName = $"TOR-DATEV-Kassenbuch-Z{z.ZNumber:000000}-{z.PeriodTo:yyyyMMdd}";
        var csvPath = Path.Combine(ExportDirectory, baseName + ".csv");

        if (File.Exists(csvPath) && existing is null)
            throw new InvalidOperationException(
                "DATEV-CSV existiert bereits ohne zugehörigen Export-Log. Datei wird nicht überschrieben.");

        var csv = Render(rows);
        await File.WriteAllTextAsync(
            csvPath,
            csv,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            ct);

        var document = _management.ZArchiveToDocument(z);
        var pdfPath = _management.CreatePdf(document, ExportDirectory);

        var sha = Sha256(csvPath);
        var id = await InsertOrCompleteAsync(z, csvPath, sha, pdfPath, ct);

        await _audit.WriteAsync(
            actor,
            "DATEV_KASSENBUCH_ASCII_PREPARED",
            "Z_REPORT",
            z.ZNumber.ToString(CultureInfo.InvariantCulture),
            $"export_id={id}; csv={Path.GetFileName(csvPath)}; sha256={sha}; rows={rows.Count}",
            ct);

        return await GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("DATEV-ASCII-Export konnte nicht erneut geladen werden.");
    }

    public async Task<DatevKassenbuchAsciiExport> SendToSteuerberaterAsync(
        DatevKassenbuchAsciiExport export,
        string actor,
        CancellationToken ct = default)
    {
        VerifyHash(export.CsvPath, export.CsvSha256);

        var values = await _settings.LoadAllAsync(ct);
        var recipient = values.GetValueOrDefault(SettingRecipient, "").Trim();
        if (string.IsNullOrWhiteSpace(recipient))
            recipient = values.GetValueOrDefault("reports.email.recipient", "").Trim();

        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException(
                "Steuerberater-E-Mail fehlt. Unter EINSTELLUNGEN → DATEV speichern.");

        var subject = $"TOR POS · DATEV Kassenbuch · Z {export.ZNumber:000000}";
        var body =
            $"Anbei der DATEV Kassenbuch Standard-ASCII/CSV Export und der zugehörige Z-Bericht.\r\n\r\n" +
            $"Z-Nummer: {export.ZNumber:000000}\r\n" +
            $"CSV SHA-256: {export.CsvSha256}\r\n\r\n" +
            "Hinweis: Die CSV ist für den DATEV Kassenbuch online Standard-ASCII Import vorbereitet. " +
            "Kartenzahlungen sind keine Kassenbewegungen und deshalb nicht Bestandteil dieser Kassenbuch-Datei.";

        try
        {
            await _email.SendFilesAsync(
                recipient,
                subject,
                body,
                new[] { export.CsvPath, export.PdfPath },
                ct);

            await UpdateEmailStateAsync(
                export.Id,
                recipient,
                "SENT",
                "",
                sentAt: DateTimeOffset.Now,
                ct);

            await _audit.WriteAsync(
                actor,
                "DATEV_KASSENBUCH_ASCII_EMAILED",
                "DATEV_ASCII_EXPORT",
                export.Id.ToString(CultureInfo.InvariantCulture),
                $"z={export.ZNumber}; recipient={recipient}; sha256={export.CsvSha256}",
                ct);
        }
        catch (Exception ex)
        {
            await UpdateEmailStateAsync(
                export.Id,
                recipient,
                "SEND_FAILED",
                Safe(ex.Message),
                sentAt: null,
                ct);

            await _audit.WriteAsync(
                actor,
                "DATEV_KASSENBUCH_ASCII_EMAIL_FAILED",
                "DATEV_ASCII_EXPORT",
                export.Id.ToString(CultureInfo.InvariantCulture),
                $"z={export.ZNumber}; recipient={recipient}; error={Safe(ex.Message)}",
                ct);
            throw;
        }

        return await GetByIdAsync(export.Id, ct)
            ?? throw new InvalidOperationException("DATEV-ASCII-Mailstatus konnte nicht erneut geladen werden.");
    }

    public async Task<IReadOnlyList<DatevKassenbuchAsciiExport>> GetJournalAsync(
        int max = 200,
        CancellationToken ct = default)
    {
        max = Math.Clamp(max, 1, 1000);
        var rows = new List<DatevKassenbuchAsciiExport>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,csv_path,csv_sha256,pdf_path,
                   email_state,email_recipient,email_attempts,email_sent_at,last_error
            FROM datev_kassenbuch_ascii_exports
            ORDER BY z_number DESC
            LIMIT $max;
            """;
        q.Parameters.AddWithValue("$max", max);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(Read(r));
        return rows;
    }

    public async Task<ReportDocument> BuildJournalReportAsync(CancellationToken ct = default)
    {
        var rows = await GetJournalAsync(200, ct);
        var lines = new List<string>
        {
            "DATEV KASSENBUCH · STANDARD-ASCII / CSV",
            "Standard-Dateiweg ohne DATEV Developer-API in TOR POS.",
            "",
            "Z-Nr. | CSV | E-Mail | Empfänger | Fehler"
        };

        lines.AddRange(rows.Select(x =>
            $"Z {x.ZNumber:000000} | {Path.GetFileName(x.CsvPath)} | {x.EmailState} | " +
            $"{x.EmailRecipient} | {x.LastError}"));

        if (rows.Count == 0)
            lines.Add("Noch keine DATEV-Kassenbuch-Datei erzeugt.");

        return new ReportDocument(
            "DATEV KASSENBUCH · EXPORTJOURNAL",
            lines,
            DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<DatevKassenbuchAsciiRow>> BuildRowsAsync(
        ZArchiveRow z,
        CancellationToken ct = default)
    {
        var sales = new List<SaleCashTaxData>();
        // Match the generated created_at_utc column exactly:
        // strftime('%Y-%m-%dT%H:%M:%fZ', ...).
        var fromUtc = z.PeriodFrom.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
        var toUtc = z.PeriodTo.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);

        await using var c = _db.OpenConnection();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,receipt_number,created_at,
                       COALESCE(transaction_type,'SALE'),
                       payment_method,total_cents,
                       COALESCE(cash_portion_cents,0),
                       COALESCE(card_portion_cents,0)
                FROM sales
                WHERE created_at_utc >= $from AND created_at_utc <= $to
                  AND COALESCE(transaction_type,'SALE') IN ('SALE','STORNO','RETURN')
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var total = r.GetInt64(5);
                var cashPortion = r.GetInt64(6);
                var cardPortion = r.GetInt64(7);
                if (cashPortion == 0 && cardPortion == 0 &&
                    string.Equals(r.GetString(4), "CASH", StringComparison.OrdinalIgnoreCase))
                    cashPortion = total;

                // V-1: a Leergut payout is a sale with a negative cash portion.
                // The Z counts it in Bar, so the Kassenbuch must as well -
                // skipping it made the Z/DATEV reconciliation fail.
                if (cashPortion == 0)
                    continue;

                sales.Add(new SaleCashTaxData(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    DateTimeOffset.Parse(r.GetString(2)),
                    r.GetString(3),
                    cashPortion));
            }
        }

        var grossBySale = new Dictionary<long,Dictionary<decimal,long>>();
        foreach (var sale in sales)
        {
            var byRate = new Dictionary<decimal,long>();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END,
                       unit_price_cents,vat_rate,line_total_cents,
                       COALESCE(vat_allocations_json,'')
                FROM sale_items
                WHERE sale_id=$sale
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$sale", sale.Id);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var line = new CartLine
                {
                    Quantity = QuantityStorage.FromMilli(r.GetInt64(0)),
                    UnitPriceCents = r.GetInt64(1),
                    VatRate = Convert.ToDecimal(r.GetDouble(2)),
                    VatAllocations = VatAllocationStorage.Deserialize(r.GetString(4)),
                    // V-1: stored line total, as on the Z and the receipt.
                    PersistedLineTotalCents = r.GetInt64(3)
                };

                foreach (var bucket in MenuVatPolicy.LineAllocations(line))
                    byRate[bucket.VatRate] =
                        checked(byRate.GetValueOrDefault(bucket.VatRate) + bucket.GrossCents);
            }
            grossBySale[sale.Id] = byRate;
        }

        var dailyTax = new Dictionary<(DateOnly Day, decimal Vat), long>();
        long expectedCashSales = 0;
        foreach (var sale in sales)
        {
            var sign = string.Equals(sale.TransactionType, "SALE", StringComparison.OrdinalIgnoreCase)
                ? 1L
                : -1L;
            var cash = sale.CashPortionCents;
            expectedCashSales = checked(expectedCashSales + sign * cash);

            var byRate = grossBySale[sale.Id];
            var grossTotal = byRate.Values.Sum();
            if (grossTotal == 0)
                throw new InvalidOperationException(
                    $"Bon {sale.ReceiptNumber:000000}: MwSt.-Basis für DATEV-Kassenbuch fehlt.");

            var ordered = byRate.OrderBy(x => x.Key).ToArray();
            long assigned = 0;
            for (var index = 0; index < ordered.Length; index++)
            {
                var pair = ordered[index];
                var abs = index == ordered.Length - 1
                    ? cash - assigned
                    : (long)Math.Round(
                        (decimal)cash * pair.Value / grossTotal,
                        MidpointRounding.AwayFromZero);
                assigned += abs;
                var key = (DateOnly.FromDateTime(sale.CreatedAt.LocalDateTime), pair.Key);
                dailyTax[key] = checked(
                    dailyTax.GetValueOrDefault(key) + sign * abs);
            }
        }

        if (expectedCashSales != z.CashCents)
            throw new InvalidOperationException(
                $"DATEV-Kassenbuch-Abgleich fehlgeschlagen: Z Bar {Money(z.CashCents)} €, " +
                $"berechnet {Money(expectedCashSales)} €.");

        var result = new List<DatevKassenbuchAsciiRow>();
        foreach (var item in dailyTax
                     .Where(x => x.Value != 0)
                     .OrderBy(x => x.Key.Day)
                     .ThenBy(x => x.Key.Vat))
        {
            result.Add(new DatevKassenbuchAsciiRow(
                "EUR",
                item.Value,
                $"Z{z.ZNumber:000000}-{VatText(item.Key.Vat)}",
                item.Key.Day,
                $"Tageslosung Z {z.ZNumber:000000} · MwSt {VatText(item.Key.Vat)} %",
                item.Key.Vat,
                "",
                "",
                "",
                "",
                "",
                "",
                $"TOR POS · Z {z.ZNumber:000000} · Fiskalstatus {z.FiscalStatus}"));
        }

        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id,created_at,movement_type,amount_cents,reason
                FROM cash_movements
                WHERE created_at_utc >= $from AND created_at_utc <= $to
                  AND movement_type IN ('EINLAGE','ENTNAHME')
                  AND fiscal_mode <> 'TEST_ONLY'
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$from", fromUtc);
            q.Parameters.AddWithValue("$to", toUtc);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt64(0);
                var at = DateTimeOffset.Parse(r.GetString(1));
                var type = r.GetString(2).Trim().ToUpperInvariant();
                var amount = Math.Abs(r.GetInt64(3));
                var signed = type == "ENTNAHME" ? -amount : amount;
                if (signed == 0)
                    continue;

                var text = type.Contains("ENTNAH", StringComparison.OrdinalIgnoreCase)
                    ? "Kassenentnahme"
                    : type.Contains("EINLAG", StringComparison.OrdinalIgnoreCase)
                        ? "Kasseneinlage"
                        : "Kassenbewegung " + type;

                result.Add(new DatevKassenbuchAsciiRow(
                    "EUR",
                    signed,
                    $"K{z.ZNumber:000000}-{id}",
                    DateOnly.FromDateTime(at.LocalDateTime),
                    text,
                    null,
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    string.IsNullOrWhiteSpace(r.GetString(4))
                        ? $"TOR POS · Z {z.ZNumber:000000}"
                        : r.GetString(4).Trim()));
            }
        }

        return result
            .OrderBy(x => x.BookingDate)
            .ThenBy(x => x.ReceiptNumber, StringComparer.Ordinal)
            .ToArray();
    }

    public static string Render(IReadOnlyList<DatevKassenbuchAsciiRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var row in rows)
        {
            if (row.SignedAmountCents == 0)
                continue;

            var fields = new[]
            {
                row.Currency,
                SignedMoney(row.SignedAmountCents),
                row.ReceiptNumber,
                row.BookingDate.ToString("ddMM", CultureInfo.InvariantCulture),
                Limit(row.BookingText, 60),
                row.VatRate is decimal vat ? VatText(vat) : "",
                row.BookingKey,
                row.CounterAccount,
                row.Cost1,
                row.Cost2,
                row.Quantity,
                row.Discount,
                Limit(row.Message, 250)
            };

            if (fields.Length != 13)
                throw new InvalidOperationException("DATEV Standard-ASCII muss genau 13 Spalten enthalten.");

            sb.Append(string.Join(';', fields.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    private Task<long> InsertOrCompleteAsync(
        ZArchiveRow z,
        string csvPath,
        string sha,
        string pdfPath,
        CancellationToken ct)
        => IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO datev_kassenbuch_ascii_exports(
                  z_archive_id,z_number,created_at,csv_path,csv_sha256,pdf_path,email_state)
                VALUES($za,$zn,$at,$csv,$sha,$pdf,'READY')
                ON CONFLICT(z_archive_id) DO UPDATE SET
                  csv_path=CASE WHEN datev_kassenbuch_ascii_exports.csv_sha256='' THEN excluded.csv_path ELSE datev_kassenbuch_ascii_exports.csv_path END,
                  csv_sha256=CASE WHEN datev_kassenbuch_ascii_exports.csv_sha256='' THEN excluded.csv_sha256 ELSE datev_kassenbuch_ascii_exports.csv_sha256 END,
                  pdf_path=CASE WHEN datev_kassenbuch_ascii_exports.csv_sha256='' THEN excluded.pdf_path ELSE datev_kassenbuch_ascii_exports.pdf_path END,
                  email_state=CASE WHEN datev_kassenbuch_ascii_exports.csv_sha256='' THEN 'READY' ELSE datev_kassenbuch_ascii_exports.email_state END;
                SELECT id FROM datev_kassenbuch_ascii_exports WHERE z_archive_id=$za;
                """;
            q.Parameters.AddWithValue("$za", z.Id);
            q.Parameters.AddWithValue("$zn", z.ZNumber);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$csv", csvPath);
            q.Parameters.AddWithValue("$sha", sha);
            q.Parameters.AddWithValue("$pdf", pdfPath);
            return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        });

    private Task UpdateEmailStateAsync(
        long id,
        string recipient,
        string state,
        string error,
        DateTimeOffset? sentAt,
        CancellationToken ct)
        => IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE datev_kassenbuch_ascii_exports
                SET email_state=$state,
                    email_recipient=$recipient,
                    email_attempts=email_attempts+1,
                    email_sent_at=COALESCE($sent,email_sent_at),
                    last_error=$error
                WHERE id=$id;
                """;
            q.Parameters.AddWithValue("$state", state);
            q.Parameters.AddWithValue("$recipient", recipient);
            q.Parameters.AddWithValue("$sent", sentAt?.ToString("O") ?? (object)DBNull.Value);
            q.Parameters.AddWithValue("$error", error);
            q.Parameters.AddWithValue("$id", id);
            await q.ExecuteNonQueryAsync(ct);
        });

    private async Task<DatevKassenbuchAsciiExport?> FindByZAsync(long zId, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,csv_path,csv_sha256,pdf_path,
                   email_state,email_recipient,email_attempts,email_sent_at,last_error
            FROM datev_kassenbuch_ascii_exports WHERE z_archive_id=$z;
            """;
        q.Parameters.AddWithValue("$z", zId);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private async Task<DatevKassenbuchAsciiExport?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,csv_path,csv_sha256,pdf_path,
                   email_state,email_recipient,email_attempts,email_sent_at,last_error
            FROM datev_kassenbuch_ascii_exports WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static DatevKassenbuchAsciiExport Read(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetInt64(2),
            DateTimeOffset.Parse(r.GetString(3)),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetInt32(9),
            r.IsDBNull(10) || string.IsNullOrWhiteSpace(r.GetString(10))
                ? null
                : DateTimeOffset.Parse(r.GetString(10)),
            r.GetString(11));

    private static string Limit(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }

    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (!text.Contains(';') &&
            !text.Contains('"') &&
            !text.Contains('\r') &&
            !text.Contains('\n'))
            return text;

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SignedMoney(long cents)
    {
        var sign = cents >= 0 ? "+" : "-";
        var absolute = Math.Abs(cents);
        return sign + (absolute / 100m).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"));
    }

    private static string VatText(decimal vat) =>
        vat.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"));

    private static string Money(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyHash(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("DATEV-CSV-Datei fehlt.");
        var actual = Sha256(path);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "DATEV-CSV wurde nach der Erstellung verändert. Versand/Retry ist gesperrt.");
    }

    private static string Safe(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : text.Length <= 700 ? text : text[..700];

    private sealed record SaleCashTaxData(
        long Id,
        long ReceiptNumber,
        DateTimeOffset CreatedAt,
        string TransactionType,
        long CashPortionCents);
}
