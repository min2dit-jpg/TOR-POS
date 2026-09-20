using TorPos.Core;

public static class R173ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var office = ReceiptPrinterProfiles.Detect(
            "EPSON367671 (ET-4850 Series)",
            "EPSON ET-4850 Series",
            "WSD-1234");

        assert(
            office.Manufacturer == "Epson" &&
            !office.IsReceiptPrinter &&
            !office.AutoCutSupported &&
            !office.CashDrawerPortSupported &&
            office.Model == "Kein Bondruckerprofil",
            "R173 Epson ET office/multifunction queues are not misclassified as receipt printers");

        var fax = ReceiptPrinterProfiles.Detect(
            "EPSON ET-4850 Series (Fax)",
            "EPSON ET-4850 Series",
            "WSD-FAX");

        assert(
            !fax.IsReceiptPrinter && !fax.ExactModel,
            "R173 Epson fax queues remain ordinary Windows printers");

        var ambiguousReceipt = ReceiptPrinterProfiles.Detect(
            "EPSON Receipt Printer",
            "EPSON POS Driver",
            "USB001");

        assert(
            ambiguousReceipt.IsReceiptPrinter &&
            !ambiguousReceipt.ExactModel &&
            ambiguousReceipt.AutoCutSupported &&
            ambiguousReceipt.CashDrawerPortSupported,
            "R173 ambiguous Epson POS/receipt queues remain usable without guessing a concrete model");

        var service = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/StarMcPrint3PrinterService.cs"));

        assert(
            service.Contains("EntryPoint = \"OpenPrinterW\"", StringComparison.Ordinal) &&
            service.Contains("EntryPoint = \"GetPrinterW\"", StringComparison.Ordinal) &&
            service.Contains("CharSet = CharSet.Unicode", StringComparison.Ordinal) &&
            service.Contains("PtrToStringUni", StringComparison.Ordinal),
            "R173 Windows spooler metadata uses one consistent Unicode API/decoder path");

        var assistant = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/PrinterSetupWindow.cs"));

        assert(
            assistant.Contains("var usableReceiptPrinter = d.IsReceiptPrinter && d.Ready != false;", StringComparison.Ordinal) &&
            assistant.Contains("Büro-/PDF-/Faxdrucker bleiben sichtbar", StringComparison.Ordinal) &&
            assistant.Contains("kann hier nicht als Bondrucker übernommen werden", StringComparison.Ordinal),
            "R173 office/PDF/fax queues stay visible for diagnosis but cannot be accepted as receipt printers");

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

        throw new FileNotFoundException(
            $"R173 review could not locate repository file: {relativePath}");
    }
}
