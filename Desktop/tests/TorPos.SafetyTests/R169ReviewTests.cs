using TorPos.Core;

public static class R169ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var t20 = ReceiptPrinterProfiles.Detect(
            "EPSON TM-T20III Receipt",
            "EPSON TM-T20III Advanced Printer Driver",
            "USB001");
        assert(
            t20.Manufacturer == "Epson" && t20.Model == "TM-T20III" &&
            t20.ExactModel && t20.PaperWidthMm == 80,
            "R169 Epson TM-T20III is recognized exactly as an 80 mm receipt printer");

        var t88 = ReceiptPrinterProfiles.Detect(
            "EPSON TM-T88VII",
            "EPSON Advanced Printer Driver for TM-T88VII",
            "IP_192.168.10.44");
        assert(
            t88.Model == "TM-T88VII" && t88.ExactModel && t88.ConnectionType == "LAN / IP",
            "R169 Epson TM-T88VII is recognized through a Windows TCP/IP queue");

        var m30 = ReceiptPrinterProfiles.Detect(
            "TM-m30III",
            "EPSON TM-m30III",
            "WSD-123");
        assert(
            m30.Manufacturer == "Epson" && m30.Model == "TM-m30III" &&
            m30.ConnectionType == "NETZWERK / WSD",
            "R169 Epson TM-m30III is recognized without guessing from the port");

        var mc3 = ReceiptPrinterProfiles.Detect(
            "Star MCP31CBI",
            "Star mC-Print3",
            "USB002");
        assert(
            mc3.Manufacturer == "Star" && mc3.Model == "mC-Print3" &&
            mc3.ExactModel && mc3.PaperWidthMm == 80,
            "R169 Star MCP31/mC-Print3 is recognized exactly and keeps the 80 mm profile");

        var mc2 = ReceiptPrinterProfiles.Detect(
            "Star MCP20",
            "Star mC-Print2",
            "USB003");
        assert(
            mc2.Model == "mC-Print2" && mc2.PaperWidthMm == 58,
            "R169 Star mC-Print2 keeps its separate 58 mm profile");

        var tsp = new[]
        {
            ReceiptPrinterProfiles.Detect("Star TSP143III", "Star TSP100III", "USB004").Model,
            ReceiptPrinterProfiles.Detect("Star TSP143IV", "Star TSP100IV", "USB005").Model,
            ReceiptPrinterProfiles.Detect("Star TSP654II", "Star TSP650II", "USB006").Model
        };
        assert(
            tsp.SequenceEqual(new[] { "TSP100III", "TSP100IV", "TSP650II" }),
            "R169 reviewed Star TSP100III, TSP100IV and TSP650II families have stable model profiles");

        var genericEpson = ReceiptPrinterProfiles.Detect(
            "EPSON Receipt Printer",
            "EPSON POS Driver",
            "USB007");
        assert(
            genericEpson.IsReceiptPrinter && !genericEpson.ExactModel &&
            genericEpson.Model == "Modell nicht eindeutig",
            "R169 generic Epson queues are recognized as Epson but never silently assigned a concrete model");

        var office = ReceiptPrinterProfiles.Detect(
            "Microsoft Print to PDF",
            "Microsoft Print To PDF",
            "PORTPROMPT:");
        assert(
            !office.IsReceiptPrinter && !office.ExactModel &&
            !office.AutoCutSupported && !office.CashDrawerPortSupported,
            "R169 ordinary Windows/A4 queues are not misclassified as receipt printers");

        assert(
            ReceiptPrinterProfiles.DetectConnection("USB001") == "USB" &&
            ReceiptPrinterProfiles.DetectConnection("IP_10.0.0.50") == "LAN / IP" &&
            ReceiptPrinterProfiles.DetectConnection("WSD-abcd") == "NETZWERK / WSD",
            "R169 USB, TCP/IP and WSD Windows ports are classified without vendor SDKs");

        var service = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/StarMcPrint3PrinterService.cs"));
        assert(
            service.Contains("GetInstalledPrinterDevices()", StringComparison.Ordinal) &&
            service.Contains("GetPrinter(handle, 2", StringComparison.Ordinal) &&
            service.Contains("pDriverName", StringComparison.Ordinal) &&
            service.Contains("pPortName", StringComparison.Ordinal),
            "R169 discovery reads Windows queue, driver and port metadata instead of relying on display name alone");

        var drawerMethod = Slice(
            service,
            "public async Task TestCashDrawerAsync",
            "public Task PrintReceiptAsync");
        assert(
            drawerMethod.Contains("RawPrinterIo.SendRaw", StringComparison.Ordinal) &&
            drawerMethod.Contains("StarPrntRawCommands.OpenCashDrawer()", StringComparison.Ordinal) &&
            !drawerMethod.Contains("EnqueueAsync(", StringComparison.Ordinal) &&
            !drawerMethod.Contains("PrintNow(", StringComparison.Ordinal),
            "R169 cash-drawer test sends one raw drawer pulse without creating a receipt print job");

        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var assistant = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PrinterSetupWindow.cs"));
        var firstRun = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/FirstRunSetupWindow.cs"));
        assert(
            settings.Contains("DRUCKER-ZENTRALE · EPSON / STAR AUTOMATISCH ERKENNEN", StringComparison.Ordinal) &&
            settings.Contains("KASSENSCHUBLADE TESTEN", StringComparison.Ordinal) &&
            assistant.Contains("DRUCKER AUTOMATISCH SUCHEN", StringComparison.Ordinal) &&
            assistant.Contains("Modell blieb absichtlich 'nicht eindeutig'", StringComparison.Ordinal) &&
            firstRun.Contains("AUTOMATISCH ERKENNEN", StringComparison.Ordinal),
            "R169 settings and first-run expose automatic printer detection plus an explicit drawer connection test with fail-safe model handling");

        return Task.CompletedTask;
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

        throw new FileNotFoundException($"R169 review could not locate repository file: {relativePath}");
    }
}
