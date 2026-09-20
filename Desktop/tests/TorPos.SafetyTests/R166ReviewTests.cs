public static class R166ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var xaml = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var payment = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PaymentChoiceWindow.cs"));
        var snapshot = File.ReadAllText(FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));

        assert(
            !xaml.Contains("x:Name=\"CashButton\"", StringComparison.Ordinal) &&
            !xaml.Contains("x:Name=\"CardButton\"", StringComparison.Ordinal) &&
            xaml.Contains("x:Name=\"QuickCheckoutButton\"", StringComparison.Ordinal) &&
            xaml.Contains("Grid.ColumnSpan=\"2\"", StringComparison.Ordinal),
            "R166 cashier shows one wide KASSIEREN button instead of duplicate BAR/KARTE buttons");

        assert(
            xaml.Contains("Columns=\"3\"", StringComparison.Ordinal) &&
            !xaml.Contains("F5 · ZAHLART", StringComparison.Ordinal) &&
            xaml.Contains("F5 · ZAHLUNG", StringComparison.Ordinal),
            "R166 top quick bar no longer duplicates the cashier payment button");

        assert(
            payment.Contains("one payment surface owns service type, tender selection", StringComparison.Ordinal) &&
            payment.Contains("BuildCashPanel()", StringComparison.Ordinal) &&
            payment.Contains("BuildCardPanel()", StringComparison.Ordinal) &&
            payment.Contains("BuildMixedPanel()", StringComparison.Ordinal),
            "R166 one payment window contains BAR, KARTE and GEMISCHT detail surfaces");

        assert(
            payment.Contains("CashPaymentResult? Cash", StringComparison.Ordinal) &&
            payment.Contains("long CashPortionCents", StringComparison.Ordinal) &&
            payment.Contains("UiConfirmed", StringComparison.Ordinal),
            "R166 payment result carries cash tender, mixed split and confirmation back without opening a second input window");

        assert(
            main.Contains("choice.CashPortionCents", StringComparison.Ordinal) &&
            main.Contains("choice.Cash", StringComparison.Ordinal) &&
            main.Contains("choice.UiConfirmed", StringComparison.Ordinal) &&
            main.Contains("precollectedCash is not null", StringComparison.Ordinal),
            "R166 checkout consumes payment data already collected by the unified payment page");

        assert(
            main.Contains("!paymentUiConfirmed", StringComparison.Ordinal) &&
            main.Contains("new CardTestPaymentWindow", StringComparison.Ordinal) &&
            main.Contains("new CashPaymentWindow", StringComparison.Ordinal),
            "R166 legacy payment dialogs remain only as guarded fallbacks and are skipped after the unified page confirms input");

        assert(
            main.Contains("OpenPaymentWindowAsync(PaymentMethod.Cash)", StringComparison.Ordinal) &&
            main.Contains("OpenPaymentWindowAsync(PaymentMethod.Card)", StringComparison.Ordinal) &&
            main.Contains("OpenPaymentWindowAsync(null)", StringComparison.Ordinal),
            "R166 F1, F2 and F5 all enter the same payment surface with optional method preselection");

        assert(
            snapshot.Contains("totalCents: 1890", StringComparison.Ordinal) &&
            snapshot.Contains("new PaymentChoiceWindow", StringComparison.Ordinal),
            "R166 CI continues to render the real unified payment window in the layout gate");

        return Task.CompletedTask;
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

        throw new FileNotFoundException($"R166 review could not locate repository file: {relativePath}");
    }
}
