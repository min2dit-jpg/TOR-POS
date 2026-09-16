using TorPos.Infrastructure;

// R89: a live Windows printer status check now runs right before EVERY
// print job in StarMcPrint3PrinterService.ProcessQueueAsync, not just once
// at the initial Geräte-Manager probe - catches the printer having gone
// offline/out of paper since that probe, without wasting a spooler
// attempt on it (reuses the already-tested PrinterStatusProblem mapping
// from R58, just calls it at a second point).
//
// There is no way to make Windows actually report a broken printer inside
// this test suite without real hardware, so this cannot exercise the true
// positive (a printer that IS reporting a problem) end-to-end. What it DOES
// verify is the safety property the whole rest of the print-related test
// suite silently depends on: when TryReadWindowsPrinterStatus can't get a
// live status for a name (the normal case for every synthetic printer name
// used across this suite, and for real printers/drivers that simply don't
// support status queries), the new check must fail OPEN - printing
// proceeds normally - not fail closed and block every print job.
public static class R89ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert, Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r89-preprint-status");
        Directory.CreateDirectory(dir);
        var journal = new PrintJobJournal(Path.Combine(dir, "prints"));

        var nativeCalls = 0;
        var printer = new StarMcPrint3PrinterService(
            journal,
            TimeSpan.FromSeconds(5),
            () => { Interlocked.Increment(ref nativeCalls); return Task.CompletedTask; });

        await printer.PrintTestAsync("TOR-POS-R89TEST-NO-SUCH-PRINTER");
        assert(
            nativeCalls == 1,
            "R89 a pre-print status check that finds no live Windows status for the printer name fails open - the print still goes through, exactly as before this change");

        await printer.DisposeAsync();
    }
}
