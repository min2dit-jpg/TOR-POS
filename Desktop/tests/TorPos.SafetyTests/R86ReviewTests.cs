using System.Linq;
using TorPos.Core;
using TorPos.Infrastructure;

// R86/R176: direct vendor-specific StarPRNT and Epson ESC/POS cutter /
// cash-drawer commands, sent as RAW spooler jobs. R176 corrects the old
// assumption that both printer families use the same byte language.
public static class R86ReviewTests
{
    public static async Task Run(Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        assert(
            StarPrntRawCommands.EpsonEscPosPartialCut.Length == 4 &&
            StarPrntRawCommands.EpsonEscPosPartialCut[0] == 0x1D &&
            StarPrntRawCommands.EpsonEscPosPartialCut[1] == 0x56 &&
            StarPrntRawCommands.StarPrntPartialCut.SequenceEqual(
                new byte[] { 0x1B, 0x64, 0x01 }),
            "R86/R176 Epson GS V and StarPRNT ESC d partial-cut commands stay vendor-specific");

        var drawer = StarPrntRawCommands.EpsonEscPosOpenCashDrawer();
        assert(
            drawer.Length == 5 && drawer[0] == 0x1B && drawer[1] == 0x70 && drawer[2] == 0x00,
            "R86/R176 Epson default cash-drawer kick targets pin 2 via ESC p");

        var drawerPin5 = StarPrntRawCommands.EpsonEscPosOpenCashDrawer(pin: 1);
        assert(
            drawerPin5[2] == 0x01 &&
            !drawerPin5.SequenceEqual(drawer) &&
            !StarPrntRawCommands.StarPrntOpenCashDrawer(1).SequenceEqual(drawer),
            "R86/R176 drawer output selection remains configurable and StarPRNT is not treated as Epson ESC/POS");

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
            () => Task.Run(() => RawPrinterIo.SendRaw("", StarPrntRawCommands.EpsonEscPosPartialCut, "TOR POS Test")),
            "R86 a raw command with no printer selected is refused before touching the spooler");

        if (OperatingSystem.IsWindows())
        {
            await reject(
                () => Task.Run(() => RawPrinterIo.SendRaw(
                    "TOR-POS-R86TEST-NONEXISTENT-QUEUE",
                    StarPrntRawCommands.EpsonEscPosPartialCut,
                    "TOR POS Test")),
                "R86 a raw command to a printer queue that doesn't exist fails cleanly instead of hanging");
        }
    }
}
