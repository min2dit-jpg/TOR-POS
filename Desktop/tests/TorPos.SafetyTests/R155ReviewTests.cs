using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R155ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert)
    {
        var rendered = DatevKassenbuchAsciiService.Render(new[]
        {
            new DatevKassenbuchAsciiRow(
                "EUR", 123456, "Z000001-19", new DateOnly(2026,9,19),
                "Tageslosung", 19m, "", "", "", "", "", "", "TOR POS"),
            new DatevKassenbuchAsciiRow(
                "EUR", -5000, "K000001-1", new DateOnly(2026,9,19),
                "Kassenentnahme", null, "", "", "", "", "", "", "Bank")
        });
        var renderLines = rendered.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        assert(
            renderLines[0] == DatevKassenbuchAsciiService.Header &&
            renderLines[0].Count(x => x == ';') == 12,
            "R155 DATEV Kassenbuch Standard-ASCII uses the documented 13-column header in one line");

        assert(
            renderLines[1].StartsWith("EUR;+1234,56;Z000001-19;1909;", StringComparison.Ordinal) &&
            renderLines[2].StartsWith("EUR;-50,00;K000001-1;1909;", StringComparison.Ordinal),
            "R155 DATEV amounts use explicit signs/German decimal comma and BelegDatum uses TTMM");

        assert(
            renderLines.All(x => x.Count(c => c == ';') == 12),
            "R155 every DATEV Standard-ASCII data row keeps exactly 13 fields");

        var dir = Path.Combine(root, "r155-datev-ascii");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r155.db"));
        var settings = new SettingsRepository(db);
        var audit = new FakeAudit();
        var management = new BusinessManagementService(db, settings, audit);
        var email = new ReportEmailService(settings, management);
        var service = new DatevKassenbuchAsciiService(db, settings, management, email, audit);

        var now = DateTimeOffset.Now;
        await SeedSaleAsync(
            db,
            receipt: 155001,
            at: now.AddMinutes(-8),
            payment: "CASH",
            totalCents: 1000,
            cashCents: 1000,
            cardCents: 0,
            vatRate: 7m);
        await SeedSaleAsync(
            db,
            receipt: 155002,
            at: now.AddMinutes(-7),
            payment: "CARD",
            totalCents: 500,
            cashCents: 0,
            cardCents: 500,
            vatRate: 19m);

        await SeedCashMovementAsync(
            db,
            now.AddMinutes(-6),
            "EINLAGE",
            200,
            "Wechselgeld",
            "PRODUCTION");
        await SeedCashMovementAsync(
            db,
            now.AddMinutes(-5),
            "ENTNAHME",
            50,
            "Bank",
            "PRODUCTION");
        await SeedCashMovementAsync(
            db,
            now.AddMinutes(-4),
            "EINLAGE",
            9999,
            "Training darf nie exportiert werden",
            "TEST_ONLY");

        var z = await management.CreateZArchiveAsync("r155", "PRODUCTION_ALLOWED");
        assert(
            z.CashCents == 1000 && z.CardCents == 500,
            "R155 test Z has separate 10.00 EUR cash and 5.00 EUR card turnover");

        var rows = await service.BuildRowsAsync(z);
        assert(
            rows.Count == 3 &&
            rows.Any(x => x.SignedAmountCents == 1000 && x.VatRate == 7m) &&
            !rows.Any(x => x.SignedAmountCents == 500),
            "R155 Kassenbuch file includes cash turnover but excludes card-only turnover");

        assert(
            rows.Any(x => x.SignedAmountCents == 200 && x.BookingText == "Kasseneinlage") &&
            rows.Any(x => x.SignedAmountCents == -50 && x.BookingText == "Kassenentnahme") &&
            !rows.Any(x => Math.Abs(x.SignedAmountCents) == 9999),
            "R155 production Einlage/Entnahme are signed correctly and TEST_ONLY cash movements are excluded");

        var mismatchBlocked = false;
        try
        {
            await service.BuildRowsAsync(z with { CashCents = z.CashCents + 1 });
        }
        catch (InvalidOperationException ex)
        {
            mismatchBlocked = ex.Message.Contains("Abgleich", StringComparison.OrdinalIgnoreCase);
        }
        assert(
            mismatchBlocked,
            "R155 CSV generation fails closed when computed cash turnover differs from immutable Z cash total");

        var prepared = await service.PrepareAsync(z, "r155");
        var bytes = await File.ReadAllBytesAsync(prepared.CsvPath);
        var text = await File.ReadAllTextAsync(prepared.CsvPath, Encoding.UTF8);
        assert(
            bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF &&
            text.Contains("\r\n", StringComparison.Ordinal) &&
            prepared.CsvSha256.Length == 64 &&
            File.Exists(prepared.PdfPath),
            "R155 generated DATEV file is UTF-8 BOM + CRLF, hash-protected, and paired with the Z PDF");

        var again = await service.PrepareAsync(z, "r155");
        assert(
            again.Id == prepared.Id &&
            again.CsvPath == prepared.CsvPath &&
            again.CsvSha256 == prepared.CsvSha256,
            "R155 repeat export reuses the same immutable CSV/hash for one Z close");

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('datev_kassenbuch_ascii_exports')
                WHERE name IN ('csv_sha256','email_state','email_recipient','email_attempts');
                """;
            assert(
                Convert.ToInt32(q.ExecuteScalar(), CultureInfo.InvariantCulture) == 4,
                "R155 schema migration 23 stores immutable DATEV file identity and email delivery state");
        }

        var deleteBlocked = false;
        try
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "DELETE FROM datev_kassenbuch_ascii_exports WHERE id=$id;";
            q.Parameters.AddWithValue("$id", prepared.Id);
            q.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            deleteBlocked = true;
        }
        assert(
            deleteBlocked,
            "R155 DATEV export audit record cannot be deleted");

        await settings.SaveManyAsync(new Dictionary<string,string>
        {
            [DatevKassenbuchAsciiService.SettingRecipient] = "steuerberater@example.invalid"
        });
        await File.AppendAllTextAsync(prepared.CsvPath, "tampered");
        var tamperBlocked = false;
        try
        {
            await service.SendToSteuerberaterAsync(prepared, "r155");
        }
        catch (InvalidOperationException ex)
        {
            tamperBlocked = ex.Message.Contains("verändert", StringComparison.OrdinalIgnoreCase);
        }
        assert(
            tamperBlocked,
            "R155 modified DATEV CSV is blocked before any email transmission");

        assert(
            audit.Events.Contains("DATEV_KASSENBUCH_ASCII_PREPARED"),
            "R155 DATEV CSV creation is written to the audit trail");
    }

    private static async Task SeedSaleAsync(
        SqliteDatabase db,
        long receipt,
        DateTimeOffset at,
        string payment,
        long totalCents,
        long cashCents,
        long cardCents,
        decimal vatRate)
    {
        await using var c = db.OpenConnection();
        await using var tx = await c.BeginTransactionAsync();
        long saleId;

        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO sales(
                  receipt_number,created_at,payment_method,
                  subtotal_cents,discount_cents,total_cents,fiscal_status,
                  transaction_type,cash_portion_cents,card_portion_cents,started_at)
                VALUES(
                  $r,$at,$payment,
                  $total,0,$total,'PRODUCTION_ALLOWED',
                  'SALE',$cash,$card,$at);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$r", receipt);
            q.Parameters.AddWithValue("$at", at.ToString("O"));
            q.Parameters.AddWithValue("$payment", payment);
            q.Parameters.AddWithValue("$total", totalCents);
            q.Parameters.AddWithValue("$cash", cashCents);
            q.Parameters.AddWithValue("$card", cardCents);
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }

        await using (var q = c.CreateCommand())
        {
            q.Transaction = (SqliteTransaction)tx;
            q.CommandText = """
                INSERT INTO sale_items(
                  sale_id,product_id,product_name,quantity,
                  unit_price_cents,vat_rate,line_total_cents)
                VALUES($sale,$product,$name,1,$total,$vat,$total);
                """;
            q.Parameters.AddWithValue("$sale", saleId);
            q.Parameters.AddWithValue("$product", receipt);
            q.Parameters.AddWithValue("$name", "R155 Test");
            q.Parameters.AddWithValue("$total", totalCents);
            q.Parameters.AddWithValue("$vat", Convert.ToDouble(vatRate));
            await q.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static async Task SeedCashMovementAsync(
        SqliteDatabase db,
        DateTimeOffset at,
        string type,
        long amountCents,
        string reason,
        string fiscalMode)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO cash_movements(
              created_at,movement_type,amount_cents,reason,actor,fiscal_mode)
            VALUES($at,$type,$amount,$reason,'r155',$mode);
            """;
        q.Parameters.AddWithValue("$at", at.ToString("O"));
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$amount", amountCents);
        q.Parameters.AddWithValue("$reason", reason);
        q.Parameters.AddWithValue("$mode", fiscalMode);
        await q.ExecuteNonQueryAsync();
    }

    private sealed class FakeAudit : IAuditLog
    {
        public List<string> Events { get; } = new();

        public Task WriteAsync(
            string actor,
            string eventType,
            string entityType,
            string entityId,
            string details,
            CancellationToken ct = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }

        public Task<string> ExportCsvAsync(
            string targetPath,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            CancellationToken ct = default) =>
            Task.FromResult(targetPath);
    }
}
