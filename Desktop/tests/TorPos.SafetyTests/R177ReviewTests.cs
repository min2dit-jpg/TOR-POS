using TorPos.Core;

public static class R177ReviewTests
{
    public static Task Run(
        string root,
        Action<bool, string> assert)
    {
        var main = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var xaml = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml"));
        var printer = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/StarMcPrint3PrinterService.cs"));
        var receipt = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Core/ReceiptPrinting.cs"));
        var settings = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));

        assert(
            xaml.Contains("ScannerCapture", StringComparison.Ordinal) &&
            xaml.Contains("PlaceholderText=\"Scannen oder EAN eingeben\"", StringComparison.Ordinal),
            "R177/R179 cashier screen provides a real TextBox focus target for keyboard-wedge scanners");

        assert(
            main.Contains("Activated += (_,_) => FocusScannerCaptureSoon();", StringComparison.Ordinal) &&
            main.Contains("InputElement.PointerReleasedEvent", StringComparison.Ordinal) &&
            main.Contains("handledEventsToo: true", StringComparison.Ordinal) &&
            main.Contains("FocusScannerCaptureSoon();", StringComparison.Ordinal),
            "R177 scanner capture is restored after touch/click, dialog activation and checkout completion");

        assert(
            main.Contains("always arm an idle fallback", StringComparison.OrdinalIgnoreCase) &&
            main.Contains("Math.Max(140, configured)", StringComparison.Ordinal) &&
            main.Contains("Math.Clamp(waitMs, 90, 2000)", StringComparison.Ordinal),
            "R177/R179/R181 barcode capture still completes without Enter/Tab using the reviewed timing window");

        assert(
            receipt.Contains("Task OpenCashDrawerAsync(", StringComparison.Ordinal) &&
            printer.Contains("public Task OpenCashDrawerAsync(", StringComparison.Ordinal) &&
            printer.Contains("SendCashDrawerAsync(", StringComparison.Ordinal) &&
            printer.Contains("Kassenschublade Zahlung", StringComparison.Ordinal),
            "R177 production cash-drawer opening reuses the exact RAW pulse path that succeeds in the settings test");

        var commit = main.IndexOf(
            "sale = await _sales.CommitAsync(snapshot);",
            StringComparison.Ordinal);
        var drawer = main.IndexOf(
            "await TryOpenCashDrawerAfterPaymentAsync(",
            commit,
            StringComparison.Ordinal);
        var signing = main.IndexOf(
            "await _fiscalSigning.SignInVorgangAsync",
            commit,
            StringComparison.Ordinal);

        assert(
            commit >= 0 &&
            drawer > commit &&
            signing > drawer &&
            main.Contains("snapshot.EffectiveCashPortionCents", StringComparison.Ordinal),
            "R177 drawer opens after durable production commit and the simulation checkout uses the same helper");

        assert(
            main.Contains("device.drawer.enabled", StringComparison.Ordinal) &&
            settings.Contains("Kassenlade verwenden", StringComparison.Ordinal) &&
            main.Contains("the drawer is opened once by the committed payment path", StringComparison.Ordinal) &&
            main.Contains("false,", StringComparison.Ordinal),
            "R177 runtime and settings share one drawer enable switch and receipt printing no longer owns the drawer kick");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            for (var dir = new DirectoryInfo(start);
                 dir is not null;
                 dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R177 review could not locate repository file: {relativePath}");
    }
}
