using System.Globalization;
using System.Reflection;
using TorPos.Core;
using TorPos.Infrastructure;

// R123: German documents keep German numbers whatever the Windows culture is.
//
// PR #1 (fix/report-currency-culture) found the STORNO report printing
// "5.00 EUR" under an English Windows culture. Reviewing it found the same
// pattern on the printed Kassenbon, the Küchenbon, the digital receipt, the
// inventory and statistics reports, the article labels and the Kassensturz
// printout. Every check here runs under en-US - the culture that exposes it.
public static class R123ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var english = CultureInfo.GetCultureInfo("en-US");

        // ---------- the shared rule ----------
        WithCulture(english, () =>
        {
            assert(
                GermanFormat.Eur(123450) == "1234,50 EUR" && GermanFormat.Amount(-500) == "-5,00",
                $"R123 GermanFormat writes a decimal comma under an English Windows culture (actual: {GermanFormat.Eur(123450)})");

            var line = GermanFormat.Line($"MwSt {5.5m:0.##}% · {1.5m:0.###} Stk.");
            assert(
                line == "MwSt 5,5% · 1,5 Stk.",
                $"R123 a whole interpolated line is formatted German, not just the amounts in it (actual: {line})");
        });

        // ---------- the printed Kassenbon ----------
        // StarMcPrint3PrinterService draws through GDI, so the page itself cannot
        // be captured here - but every amount on it goes through this one
        // private formatter, and that formatter is what was wrong.
        var printerMoney = typeof(StarMcPrint3PrinterService).GetMethod(
            "Money", BindingFlags.NonPublic | BindingFlags.Static);
        assert(printerMoney is not null, "R123 the receipt printer still has its single Money formatter");
        WithCulture(english, () =>
        {
            var printed = (string?)printerMoney?.Invoke(null, new object[] { 500L });
            assert(
                printed == "5,00 EUR",
                $"R123 the printed Kassenbon writes 5,00 EUR under an English Windows culture, not 5.00 EUR (actual: {printed})");
        });

        // ---------- the digital receipt ----------
        // R145: the document TOR Cloud shows, built from the print job.
        var job = new ReceiptPrintJob(
            123001, DateTimeOffset.Now, "R123 Laden", "Str. 1, 10115 Berlin", "", "", "", "", "Bar", 0, 413,
            new[] { new CartLine { ProductName = "R123 Lose Ware", Quantity = 1.5m, UnitPriceCents = 275, VatRate = 5.5m } });
        DigitalReceiptDocument? digital = null;
        WithCulture(english, () =>
            digital = DigitalReceiptDocument.From(job, DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 413, 0)));
        assert(
            digital is { } shown && shown.Lines[0].Quantity == "1,5" && shown.Lines[0].VatRate == "5,5" && shown.Vat[0].Rate == "5,5",
            "R123 the digital receipt shows quantity and VAT rate with a decimal comma under an English Windows culture");
        assert(
            digital is { } noPoint && !noPoint.Lines[0].Quantity.Contains('.') && !noPoint.Vat[0].Rate.Contains('.'),
            "R123 ... and no decimal point appears in those fields");

        // ---------- a report run inside the I/O queue ----------
        var dir = Path.Combine(root, "r123-culture");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r123.db"));
        var products = new ProductRepository(db);
        var category = (await products.GetCategoriesAsync()).First();
        await products.SaveWithStockAsync(
            new Product { CategoryId = category.Id, Name = "R123 Kaffee lose", Barcode = "4001230000017", BasePriceCents = 1299 },
            1.5m, 0, "r123");

        var management = new BusinessManagementService(db, new SettingsRepository(db), new AuditLogRepository(db));

        // Same technique as PR #1: the culture has to be set INSIDE the queue,
        // because IoQueue runs on its own execution context.
        var inventory = await IoQueue.RunAsync(async () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = english;
                return await management.BuildInventoryReportAsync();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });
        var row = inventory.Lines.FirstOrDefault(x => x.Contains("R123 Kaffee lose")) ?? "";
        assert(
            row.Contains("| 1,5 ") && row.Contains("12,99 EUR") && !row.Contains("1.5") && !row.Contains("12.99"),
            $"R123 the WARENBESTAND report writes stock and prices German under an English Windows culture (row: {row})");
    }

    private static void WithCulture(CultureInfo culture, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
