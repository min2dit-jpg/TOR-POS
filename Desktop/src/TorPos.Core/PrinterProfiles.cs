namespace TorPos.Core;

/// <summary>
/// R169: normalized information about a Windows printer queue. Recognition is
/// deliberately conservative: TOR only calls a model exact when the installed
/// printer/driver name contains a reviewed model token. Generic EPSON/Star
/// queues remain selectable, but are never silently relabeled as a specific
/// model.
/// </summary>
public sealed record PrinterDeviceInfo(
    string PrinterName,
    string Manufacturer,
    string Model,
    string DriverName,
    string PortName,
    string ConnectionType,
    int PaperWidthMm,
    bool AutoCutSupported,
    bool CashDrawerPortSupported,
    bool IsReceiptPrinter,
    bool ExactModel,
    string RecognitionNote,
    bool? Ready = null,
    string Status = "")
{
    public string DisplayName =>
        $"{Manufacturer} · {Model} · {ConnectionType} · {PaperWidthMm} mm · {PrinterName}";
}

public static class ReceiptPrinterProfiles
{
    private sealed record Profile(
        string Manufacturer,
        string Model,
        int PaperWidthMm,
        string[] Tokens);

    private static readonly Profile[] Known =
    {
        new("Epson", "TM-T20III", 80, new[] { "TMT20III" }),
        new("Epson", "TM-T20IV", 80, new[] { "TMT20IV" }),
        new("Epson", "TM-T20X", 80, new[] { "TMT20X" }),
        new("Epson", "TM-T88V", 80, new[] { "TMT88V" }),
        new("Epson", "TM-T88VI", 80, new[] { "TMT88VI" }),
        new("Epson", "TM-T88VII", 80, new[] { "TMT88VII" }),
        new("Epson", "TM-m30II", 80, new[] { "TMM30II" }),
        new("Epson", "TM-m30III", 80, new[] { "TMM30III" }),

        new("Star", "mC-Print2", 58, new[] { "MCP20", "MCP21", "MCP22", "MCPRINT2" }),
        new("Star", "mC-Print3", 80, new[] { "MCP30", "MCP31", "MCP32", "MCPRINT3" }),
        new("Star", "TSP100III", 80, new[] { "TSP100III", "TSP143III" }),
        new("Star", "TSP100IV", 80, new[] { "TSP100IV", "TSP143IV" }),
        new("Star", "TSP650II", 80, new[] { "TSP650II", "TSP654II" })
    };

    public static PrinterDeviceInfo Detect(
        string printerName,
        string driverName = "",
        string portName = "")
    {
        printerName = (printerName ?? "").Trim();
        driverName = (driverName ?? "").Trim();
        portName = (portName ?? "").Trim();

        var haystack = Normalize(printerName + " " + driverName);
        // Longest model token wins. This matters for families such as
        // TM-T88V / TM-T88VI / TM-T88VII where the older model token is a
        // literal prefix of the newer one.
        var match = Known
            .OrderByDescending(p => p.Tokens.Max(t => Normalize(t).Length))
            .FirstOrDefault(p =>
                p.Tokens.Any(t => haystack.Contains(Normalize(t), StringComparison.Ordinal)));

        if (match is not null)
        {
            return new PrinterDeviceInfo(
                printerName,
                match.Manufacturer,
                match.Model,
                driverName,
                portName,
                DetectConnection(portName, haystack),
                match.PaperWidthMm,
                AutoCutSupported: true,
                CashDrawerPortSupported: true,
                IsReceiptPrinter: true,
                ExactModel: true,
                RecognitionNote: "Modell aus Windows-Drucker-/Treibername eindeutig erkannt.");
        }

        if (haystack.Contains("EPSON", StringComparison.Ordinal))
        {
            var looksLikeReceiptPrinter =
                haystack.Contains("RECEIPT", StringComparison.Ordinal) ||
                haystack.Contains("POSDRIVER", StringComparison.Ordinal) ||
                haystack.Contains("ADVANCEDPRINTERDRIVER", StringComparison.Ordinal) ||
                haystack.Contains("EPSONTM", StringComparison.Ordinal) ||
                haystack.Contains("TMT", StringComparison.Ordinal) ||
                haystack.Contains("TMM", StringComparison.Ordinal);

            if (looksLikeReceiptPrinter)
            {
                return new PrinterDeviceInfo(
                    printerName,
                    "Epson",
                    "Bondrucker-Modell nicht eindeutig",
                    driverName,
                    portName,
                    DetectConnection(portName, haystack),
                    80,
                    AutoCutSupported: true,
                    CashDrawerPortSupported: true,
                    IsReceiptPrinter: true,
                    ExactModel: false,
                    RecognitionNote: "Epson-Bondrucker erkannt; Modell nicht eindeutig. Vor Übernahme am Gerät/Windows-Treiber prüfen.");
            }

            return new PrinterDeviceInfo(
                printerName,
                "Epson",
                "Kein Bondruckerprofil",
                driverName,
                portName,
                DetectConnection(portName, haystack),
                80,
                AutoCutSupported: false,
                CashDrawerPortSupported: false,
                IsReceiptPrinter: false,
                ExactModel: false,
                RecognitionNote: "Epson-Windows-Drucker erkannt, aber kein Bondruckerprofil. Büro-/Multifunktionsdrucker werden nicht automatisch als Kassenbondrucker verwendet.");
        }

        if (haystack.Contains("STAR", StringComparison.Ordinal))
        {
            return new PrinterDeviceInfo(
                printerName,
                "Star",
                "Modell nicht eindeutig",
                driverName,
                portName,
                DetectConnection(portName, haystack),
                80,
                AutoCutSupported: true,
                CashDrawerPortSupported: true,
                IsReceiptPrinter: true,
                ExactModel: false,
                RecognitionNote: "Star erkannt; Modell nicht eindeutig. Vor Übernahme am Gerät/Windows-Treiber prüfen.");
        }

        return new PrinterDeviceInfo(
            printerName,
            "Windows",
            string.IsNullOrWhiteSpace(printerName) ? "Unbekannter Drucker" : printerName,
            driverName,
            portName,
            DetectConnection(portName, haystack),
            80,
            AutoCutSupported: false,
            CashDrawerPortSupported: false,
            IsReceiptPrinter: false,
            ExactModel: false,
            RecognitionNote: "Kein geprüftes Epson-/Star-Bondruckerprofil erkannt. Manuelle Auswahl bleibt möglich.");
    }

    public static string DetectConnection(string portName, string identity = "")
    {
        var port = (portName ?? "").Trim().ToUpperInvariant();
        var all = (identity ?? "").ToUpperInvariant();

        if (port.StartsWith("USB", StringComparison.Ordinal) || all.Contains(" USB", StringComparison.Ordinal))
            return "USB";
        if (port.StartsWith("WSD", StringComparison.Ordinal))
            return "NETZWERK / WSD";
        if (port.StartsWith("IP_", StringComparison.Ordinal) ||
            port.Contains("TCP", StringComparison.Ordinal) ||
            port.Contains("192.168.", StringComparison.Ordinal) ||
            port.Contains("10.", StringComparison.Ordinal))
            return "LAN / IP";
        if (port.StartsWith("BTH", StringComparison.Ordinal) ||
            port.Contains("BLUETOOTH", StringComparison.Ordinal) ||
            all.Contains("BLUETOOTH", StringComparison.Ordinal))
            return "BLUETOOTH";
        if (port.StartsWith("COM", StringComparison.Ordinal))
            return "SERIELL";
        if (port.StartsWith("LPT", StringComparison.Ordinal))
            return "PARALLEL";
        return string.IsNullOrWhiteSpace(portName) ? "WINDOWS" : $"WINDOWS · {portName}";
    }

    private static string Normalize(string value)
    {
        var chars = (value ?? "")
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }
}
