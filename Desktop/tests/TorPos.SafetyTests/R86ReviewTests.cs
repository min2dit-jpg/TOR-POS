using System.Linq;
using TorPos.Core;
using TorPos.Infrastructure;

// R86: direct StarPRNT/ESC-POS cutter and cash-drawer commands, sent as a
// RAW spooler job right after the GDI receipt print - no vendor StarIO10
// SDK required. Closes two of the four "Printer v0.4.2" roadmap items
// (direct cutter command, direct cash-drawer command); direct bidirectional
// StarPRNT status and live paper-out monitoring stay open, since the
// Windows print spooler does not reliably support synchronous status
// read-back for a RAW datatype job the way the vendor SDK would.
public static class R86ReviewTests
{
    public static async Task Run(Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        assert(
            StarPrntRawCommands.PartialCut.Length == 4 &&
            StarPrntRawCommands.PartialCut[0] == 0x1D &&
            StarPrntRawCommands.PartialCut[1] == 0x56,
            "R86 the partial-cut command is the standard StarPRNT/ESC-POS GS V byte sequence");

        var drawer = StarPrntRawCommands.OpenCashDrawer();
        assert(
            drawer.Length == 5 && drawer[0] == 0x1B && drawer[1] == 0x70 && drawer[2] == 0x00,
            "R86 the default cash-drawer kick targets pin 2 via the standard ESC p command");

        var drawerPin5 = StarPrntRawCommands.OpenCashDrawer(pin: 1);
        assert(
            drawerPin5[2] == 0x01 && !drawerPin5.SequenceEqual(drawer),
            "R86 the cash-drawer pin selector is configurable, not hard-coded to pin 2");

        var defaultReceipt = new ReceiptPrintJob(
            0, DateTimeOffset.Now, "", "", "", "", "", "", "", 0, 0, Array.Empty<CartLine>());
        assert(
            defaultReceipt.AutoCut && !defaultReceipt.OpenCashDrawer,
            "R86 a receipt cuts by default but never kicks the drawer unless a caller explicitly asks for it");

        assert(
            new ReportPrintJob("", Array.Empty<string>(), DateTimeOffset.Now, ReportPaperFormat.Receipt80).AutoCut &&
            new KitchenPrintJob(DateTimeOffset.Now, 0, 0, "", Array.Empty<KitchenPrintLine>()).AutoCut &&
            new PickupSlipPrintJob(DateTimeOffset.Now, 0, 0).AutoCut &&
            new ErrorSlipPrintJob(DateTimeOffset.Now, "", "", "", "", "").AutoCut,
            "R86 every other print job type also cuts by default without extra caller wiring");

        assert(
            !new ReceiptPrintJob(
                0, DateTimeOffset.Now, "", "", "", "", "", "", "", 0, 0, Array.Empty<CartLine>(),
                AutoCut: false).AutoCut,
            "R86 auto-cut can still be switched off per job");

        await reject(
            () => Task.Run(() => RawPrinterIo.SendRaw("", StarPrntRawCommands.PartialCut, "TOR POS Test")),
            "R86 a raw command with no printer selected is refused before touching the spooler");

        if (OperatingSystem.IsWindows())
        {
            await reject(
                () => Task.Run(() => RawPrinterIo.SendRaw(
                    "TOR-POS-R86TEST-NONEXISTENT-QUEUE",
                    StarPrntRawCommands.PartialCut,
                    "TOR POS Test")),
                "R86 a raw command to a printer queue that doesn't exist fails cleanly instead of hanging");
        }
    }
}
