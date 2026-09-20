using TorPos.App;
using TorPos.Core;

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
    public static Task Run(Action<bool, string> assert)
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
            paymentCode.Contains("Content = \"IM HAUS\"", StringComparison.Ordinal) &&
            paymentCode.Contains("GEMISCHT\\nBAR + KARTE", StringComparison.Ordinal) &&
            paymentCode.Contains("IsEnabled = cashEnabled && cardEnabled", StringComparison.Ordinal),
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
            mainCode.Contains("if (choice.Method == PaymentMethod.Mixed)", StringComparison.Ordinal) &&
            mainCode.Contains("new MixedPaymentWindow(total)", StringComparison.Ordinal),
            "R156 choosing GEMISCHT in the payment hub continues into the existing split-amount dialog");

        assert(
            snapshotCode.Contains("new PaymentChoiceWindow(cashEnabled: true, cardEnabled: true, allowImHaus: true)", StringComparison.Ordinal) &&
            snapshotCode.Contains("LAYOUT CHECK PASSED ({sizes.Count} sizes, 6 dialogs)", StringComparison.Ordinal),
            "R156 CI renders the real payment hub and includes it in the five-size/six-dialog layout gate");

        return Task.CompletedTask;
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
