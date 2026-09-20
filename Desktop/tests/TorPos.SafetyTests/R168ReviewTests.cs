using TorPos.App;
using TorPos.Core;

public static class R168ReviewTests
{
    public static void Run(Action<bool, string> assert)
    {
        var mainAxaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var mainCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var paymentCode = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PaymentChoiceWindow.cs"));

        assert(
            mainAxaml.Contains("Grid.Row=\"4\" Grid.Column=\"2\" Grid.ColumnSpan=\"2\"", StringComparison.Ordinal) &&
            mainAxaml.Contains("x:Name=\"QuickCheckoutButton\" Classes=\"action\" Click=\"OnQuickCheckoutClick\"", StringComparison.Ordinal),
            "R168 the visible KASSIEREN button is moved to the old BAR/KARTE bottom area and spans both columns");

        assert(
            !mainAxaml.Contains("Text=\"BAR · TEST\"", StringComparison.Ordinal) &&
            !mainAxaml.Contains("Text=\"KARTE · TEST\"", StringComparison.Ordinal) &&
            !mainAxaml.Contains("F1 · BEZAHLEN", StringComparison.Ordinal) &&
            !mainAxaml.Contains("F2 · BEZAHLEN", StringComparison.Ordinal),
            "R168 the cashier no longer displays separate BAR/KARTE payment buttons");

        assert(
            mainAxaml.Contains("Columns=\"3\"", StringComparison.Ordinal) &&
            mainAxaml.Contains("Text=\"KASSIEREN → ZAHLART\"", StringComparison.Ordinal),
            "R168 the upper quick row no longer owns KASSIEREN and the cashier hint points to the single payment flow");

        assert(
            mainCode.Contains("F5 KASSIEREN", StringComparison.Ordinal) &&
            !mainCode.Contains("F1 BAR / F2 KARTE", StringComparison.Ordinal),
            "R168 user guidance no longer advertises duplicate F1/F2 tender entry points");

        assert(
            paymentCode.Contains("Eine Seite: Verkaufsart, Zahlart und Betrag", StringComparison.Ordinal) &&
            paymentCode.Contains("SelectMethod(PaymentMethod.Cash)", StringComparison.Ordinal) &&
            paymentCode.Contains("SelectMethod(PaymentMethod.Card)", StringComparison.Ordinal) &&
            paymentCode.Contains("SelectMethod(PaymentMethod.Mixed)", StringComparison.Ordinal),
            "R168 BAR, KARTE and GEMISCHT switch detail state inside one payment page");

        var openPayment = Slice(
            mainCode,
            "private async Task OpenPaymentWindowAsync",
            "private CheckoutSnapshot CaptureCheckout");
        assert(
            !openPayment.Contains("new CashPaymentWindow", StringComparison.Ordinal) &&
            !openPayment.Contains("new MixedPaymentWindow", StringComparison.Ordinal) &&
            openPayment.Contains("choice.CashTenderedCents", StringComparison.Ordinal) &&
            openPayment.Contains("choice.CashPortionCents", StringComparison.Ordinal),
            "R168 normal checkout receives cash and mixed amounts from the one payment page instead of opening second dialogs");

        assert(
            mainCode.Contains("cardConfirmedOnPaymentPage: true", StringComparison.Ordinal) &&
            mainCode.Contains("!cardConfirmedOnPaymentPage", StringComparison.Ordinal),
            "R168 simulated card confirmation is owned by the payment page and does not force a second card-test page");
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

        throw new FileNotFoundException($"R168 review could not locate repository file: {relativePath}");
    }
}
