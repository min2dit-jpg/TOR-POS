using System.Runtime.InteropServices;

namespace TorPos.Infrastructure;

/// <summary>
/// Sends raw bytes (StarPRNT/ESC-POS commands - cut, cash-drawer kick) to a
/// Windows-installed printer through the spooler's RAW datatype, bypassing
/// GDI entirely. This is the same mechanism used by countless POS
/// applications to reach a receipt printer's built-in cutter/drawer without
/// a vendor SDK - it does not require or bundle Star's StarIO10 package.
///
/// A RAW job is a separate spooler document from the GDI receipt print that
/// precedes it; on a local USB/LAN printer queue the spooler serializes
/// documents in submission order, so issuing it right after
/// PrintDocument.Print() returns reaches the printer after the receipt.
/// </summary>
internal static class RawPrinterIo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void SendRaw(string printerName, byte[] data, string documentName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows printer driver required.");
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Drucker wurde noch nicht ausgewählt.");

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero)
            throw new InvalidOperationException($"Drucker nicht verfügbar: {printerName}");

        try
        {
            var docInfo = new DOC_INFO_1
            {
                pDocName = documentName,
                pOutputFile = null,
                pDataType = "RAW"
            };

            if (StartDocPrinter(hPrinter, 1, ref docInfo) == 0)
                throw new InvalidOperationException($"RAW-Druckauftrag konnte nicht gestartet werden: {printerName}");

            try
            {
                if (!StartPagePrinter(hPrinter))
                    throw new InvalidOperationException("RAW-Seite konnte nicht gestartet werden.");

                try
                {
                    if (!WritePrinter(hPrinter, data, data.Length, out var written) || written != data.Length)
                        throw new InvalidOperationException("RAW-Kommando wurde nicht vollständig an den Drucker übergeben.");
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
