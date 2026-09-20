using System.Text;
using TorPos.App;
using TorPos.Core;
using TorPos.Infrastructure;

// R156: checkout owns Verkaufsart and GEMISCHT.
//
// The visual overflow that triggered R156 happened because AUSSER HAUS / IM HAUS
// and GEMISCHT lived in the already crowded cashier header. R156 moves both
// choices into one payment hub. This review locks the user-visible contract:
// - header no longer owns these payment controls,
// - every fresh customer starts AUSSER HAUS,
// - IM HAUS changes only the effective VAT snapshot for that sale,
// - BAR / KARTE / GEMISCHT all enter the same payment hub,
// - GEMISCHT still uses the existing cash/card split semantics,
// - the real payment dialog is part of the multi-size UI snapshot check.
public static class R156ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var food = new CartLine
        {
            ProductId = 15601,
            ProductName = "R156 Döner",
            Quantity = 1,
            UnitPriceCents = 700,
            VatRate = 7m,
            ImHausApplicable = true
        };

        var outside = new CheckoutSnapshot(
            "r156-outside",
            CheckoutSnapshot.CopyLines(new[] { food }, imHaus: false),
            0,
            PaymentMethod.Cash,
            "r156",
            null,
            ImHaus: false);

        var inside = new CheckoutSnapshot(
            "r156-inside",
            CheckoutSnapshot.CopyLines(new[] { food }, imHaus: true),
            0,
            PaymentMethod.Card,
            "r156",
            null,
            ImHaus: true);

        assert(
            !outside.ImHaus &&
            outside.Lines.Single().VatRate == 7m &&
            inside.ImHaus &&
            inside.Lines.Single().VatRate == 19m &&
            outside.TotalCents == inside.TotalCents,
            "R156 AUSSER HAUS is the normal 7% food snapshot; explicit IM HAUS changes VAT to 19% without changing the customer price");

        var mixed = new CheckoutSnapshot(
            "r156-mixed",
            CheckoutSnapshot.CopyLines(new[] { food }, imHaus: true),
            0,
            PaymentMethod.Mixed,
            "r156",
            null,
            ImHaus: true,
            CashPortionCents: 300);

        assert(
            mixed.ImHaus &&
            mixed.Method == PaymentMethod.Mixed &&
            mixed.EffectiveCashPortionCents == 300 &&
            mixed.EffectiveCardPortionCents == 400,
            "R156 GEMISCHT keeps the selected Verkaufsart and still splits the payment into the existing cash/card portions");

        var result = new PaymentChoiceResult(PaymentMethod.Mixed, ImHaus: true);
        assert(
            result.Method == PaymentMethod.Mixed && result.ImHaus,
            "R156 one payment-hub result carries Zahlart and Verkaufsart together");

        Sale SaleFrom(CheckoutSnapshot snapshot, long receipt) => new()
        {
            ReceiptNumber = receipt,
            CreatedAt = DateTimeOffset.Now,
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = snapshot.TotalCents,
            CashPortionCents = snapshot.TotalCents,
            CardPortionCents = 0,
            ImHaus = snapshot.ImHaus,
            Lines = snapshot.Lines
        };

        var outsideSale = SaleFrom(outside, 156001);
        var insideSale = SaleFrom(inside with { Method = PaymentMethod.Cash }, 156002);
        var outsideTse = Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(outsideSale));
        var insideTse = Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(insideSale));
        assert(
            outsideTse == "Beleg^0.00_7.00_0.00_0.00_0.00^7.00:Bar" &&
            insideTse == "Beleg^7.00_0.00_0.00_0.00_0.00^7.00:Bar",
            "R156 the payment-page Verkaufsart reaches TSE Kassenbeleg-V1: AUSSER HAUS uses the 7% bucket and IM HAUS the 19% bucket");

        var outsideVat = VatSummaryCalculator.Compute(outside.Lines, outside.DiscountCents);
        var insideVat = VatSummaryCalculator.Compute(inside.Lines, inside.DiscountCents);
        assert(
            outsideVat.Count == 1 && outsideVat.Single().Rate == 7m && outsideVat.Single().GrossCents == 700 &&
            insideVat.Count == 1 && insideVat.Single().Rate == 19m && insideVat.Single().GrossCents == 700,
            "R156 the receipt VAT summary follows the payment-page Verkaufsart while the gross total stays 7.00 EUR");

        var exportDir = Path.Combine(root, "r156-dsfinvk");
        Directory.CreateDirectory(exportDir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(exportDir, "r156.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R156 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/156/00001"
        });

        async Task SeedDsfinvkSaleAsync(CheckoutSnapshot snapshot, long receipt)
        {
            await Task.Delay(15);
            await using var connection = db.OpenConnection();
            await using var sale = connection.CreateCommand();
            sale.CommandText = """
                INSERT INTO sales(
                  receipt_number,created_at,payment_method,subtotal_cents,total_cents,
                  fiscal_status,transaction_type,cash_portion_cents,card_portion_cents,im_haus)
                VALUES($r,$at,'CASH',$total,$total,'TEST_FIXTURE','SALE',$total,0,$im);
                SELECT last_insert_rowid();
                """;
            sale.Parameters.AddWithValue("$r", receipt);
            sale.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            sale.Parameters.AddWithValue("$total", snapshot.TotalCents);
            sale.Parameters.AddWithValue("$im", snapshot.ImHaus ? 1 : 0);
            var saleId = Convert.ToInt64(await sale.ExecuteScalarAsync());

            await using var item = connection.CreateCommand();
            item.CommandText = """
                INSERT INTO sale_items(
                  sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents)
                VALUES($sale,$product,$name,$qty,$unit,$vat,$line);
                """;
            var line = snapshot.Lines.Single();
            item.Parameters.AddWithValue("$sale", saleId);
            item.Parameters.AddWithValue("$product", line.ProductId);
            item.Parameters.AddWithValue("$name", line.ProductName);
            item.Parameters.AddWithValue("$qty", Convert.ToDouble(line.Quantity));
            item.Parameters.AddWithValue("$unit", line.UnitPriceCents);
            item.Parameters.AddWithValue("$vat", Convert.ToDouble(line.VatRate));
            item.Parameters.AddWithValue("$line", line.LineTotalCents);
            await item.ExecuteNonQueryAsync();

            await new SaleRepository(db).RecordTseResultAsync(
                saleId,
                SaleTseResult.Outage("R156 test outage"));
        }

        await SeedDsfinvkSaleAsync(outside, 156101);
        await SeedDsfinvkSaleAsync(inside, 156102);
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit)
            .CreateZArchiveAsync("r156", "TEST");

        var exporter = new DsfinvkExportService(db, settings);
        var folder = await exporter.ExportAsync(
            DateTimeOffset.Now.AddHours(-1),
            DateTimeOffset.Now.AddMinutes(1),
            Path.Combine(exportDir, "out"));
        var dsfinvkLines = File.ReadAllLines(Path.Combine(folder, "lines.csv")).Skip(1).ToArray();
        string InHaus(long receipt) =>
            dsfinvkLines.Single(row => row.Contains($";\"{receipt}\";", StringComparison.Ordinal)).Split(';')[10];

        assert(
            InHaus(156101) == "\"0\"" && InHaus(156102) == "\"1\"",
            "R156 DSFinV-K Bonpos.INHAUS exports 0 for AUSSER HAUS and 1 for IM HAUS from the same checkout selection");

        var mainAxaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var paymentCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PaymentChoiceWindow.cs"));
        var snapshotCode = File.ReadAllText(FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));

        assert(
            !mainAxaml.Contains("x:Name=\"ImHausToggleButton\"", StringComparison.Ordinal) &&
            !mainAxaml.Contains("x:Name=\"MixedPaymentButton\"", StringComparison.Ordinal) &&
            mainAxaml.Contains("x:Name=\"LogoutButton\"", StringComparison.Ordinal),
            "R156 the cashier header no longer contains AUSSER/IM HAUS or GEMISCHT controls; ABMELDEN remains the session action");

        assert(
            paymentCode.Contains("AUSSER HAUS\\nSTANDARD", StringComparison.Ordinal) &&
            paymentCode.Contains("ChoiceButton(\"IM HAUS\")", StringComparison.Ordinal) &&
            paymentCode.Contains("GEMISCHT\\nBAR + KARTE", StringComparison.Ordinal) &&
            paymentCode.Contains("cashEnabled && cardEnabled", StringComparison.Ordinal) &&
            paymentCode.Contains("IsEnabled = enabled", StringComparison.Ordinal),
            "R156 the payment window visibly offers AUSSER HAUS, IM HAUS and GEMISCHT, with GEMISCHT enabled only when both tenders exist");

        assert(
            mainCode.Contains("private async void OnCashClick", StringComparison.Ordinal) &&
            mainCode.Contains("private async void OnCardClick", StringComparison.Ordinal) &&
            mainCode.Contains("private async void OnQuickCheckoutClick", StringComparison.Ordinal) &&
            Count(mainCode, "await OpenPaymentWindowAsync(") >= 3,
            "R156 BAR, KARTE and KASSIEREN/F5 all enter the same payment hub instead of bypassing Verkaufsart");

        var nextCustomer = Slice(
            mainCode,
            "private void PrepareNextCustomer()",
            "private ReceiptPrintJob BuildReceiptPrintJob");
        assert(
            nextCustomer.Contains("_imHaus = false;", StringComparison.Ordinal),
            "R156 every completed/cleared customer resets the next sale to AUSSER HAUS");

        assert(
            paymentCode.Contains("CashPortionCents = 0", StringComparison.Ordinal) &&
            paymentCode.Contains("SelectMethod(PaymentMethod.Mixed)", StringComparison.Ordinal) &&
            mainCode.Contains("choice.CashPortionCents", StringComparison.Ordinal) &&
            !Slice(mainCode, "private async Task OpenPaymentWindowAsync", "private CheckoutSnapshot CaptureCheckout")
                .Contains("new MixedPaymentWindow", StringComparison.Ordinal),
            "R156/R168 GEMISCHT stays in the payment hub and returns the BAR split without opening a second payment dialog");

        assert(
            snapshotCode.Contains("new PaymentChoiceWindow(cashEnabled: true, cardEnabled: true, allowImHaus: true)", StringComparison.Ordinal) &&
            snapshotCode.Contains("LAYOUT CHECK PASSED ({sizes.Count} sizes, 9 dialogs)", StringComparison.Ordinal),
            "R156 CI renders the real payment hub and includes it in the five-size/dialog layout gate");

    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start >= 0 ? start : 0, StringComparison.Ordinal);
        return start >= 0 && end > start ? text[start..end] : "";
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R156 review could not locate repository file: {relativePath}");
    }
}
