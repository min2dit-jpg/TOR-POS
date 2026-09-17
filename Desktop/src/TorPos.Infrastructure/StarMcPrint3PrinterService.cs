using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Windows printer-driver integration for Star mC-Print3.
/// Target model tested by design: MCP31CBI.
///
/// The official Star Windows driver handles USB/LAN/Bluetooth transport.
/// Printing is performed on a dedicated background queue so the Avalonia
/// UI thread is never blocked by the Windows spooler.
/// </summary>
public sealed class StarMcPrint3PrinterService : IReceiptPrinterService
{
    private readonly TimeSpan PrintTimeout;
    private readonly PrintJobJournal _printJournal;
    private readonly Func<Task>? _printOverride;
    private volatile bool _spoolerStateUncertain;
    private Task? _activePrint;
    private volatile bool _processing;
    private readonly Task _initialize;
    private volatile bool _disposed;

    private readonly Channel<QueueItem> _queue =
        Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(100)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    public StarMcPrint3PrinterService() : this(new PrintJobJournal(),TimeSpan.FromSeconds(15),null) { }
    internal StarMcPrint3PrinterService(PrintJobJournal journal,TimeSpan timeout,Func<Task>? printOverride)
    {
        _printJournal=journal; PrintTimeout=timeout; _printOverride=printOverride;
        _initialize=Task.Run(async () =>
        {
            try { _spoolerStateUncertain=(await _printJournal.GetUncertainAsync()).Count>0; }
            catch { _spoolerStateUncertain=true; }
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public string SupportedModel => "Star mC-Print3 MCP31CBI";
    public string DriverMode => "Star Windows Driver / GDI";

    private const uint PrinterStatusPaused = 0x00000001;
    private const uint PrinterStatusError = 0x00000002;
    private const uint PrinterStatusPaperJam = 0x00000008;
    private const uint PrinterStatusPaperOut = 0x00000010;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusNotAvailable = 0x00001000;
    private const uint PrinterStatusUserIntervention = 0x00100000;
    private const uint PrinterStatusDoorOpen = 0x00400000;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool GetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    internal static string? PrinterStatusProblem(uint status)
    {
        if ((status & (PrinterStatusOffline | PrinterStatusNotAvailable)) != 0)
            return "Windows meldet den Drucker als OFFLINE / NICHT VERFÜGBAR.";
        if ((status & PrinterStatusPaperOut) != 0)
            return "Windows meldet: Papier leer.";
        if ((status & PrinterStatusPaperJam) != 0)
            return "Windows meldet: Papierstau.";
        if ((status & PrinterStatusDoorOpen) != 0)
            return "Windows meldet: Druckerabdeckung offen.";
        if ((status & PrinterStatusPaused) != 0)
            return "Windows meldet: Drucker angehalten / pausiert.";
        if ((status & PrinterStatusUserIntervention) != 0)
            return "Windows meldet: Eingriff am Drucker erforderlich.";
        if ((status & PrinterStatusError) != 0)
            return "Windows meldet einen Druckerfehler.";
        return null;
    }

    private static bool TryReadWindowsPrinterStatus(string printerName, out uint status)
    {
        status = 0;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(printerName))
            return false;

        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero) || handle == IntPtr.Zero)
            return false;

        try
        {
            _ = GetPrinter(handle, 6, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return false;

            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetPrinter(handle, 6, buffer, needed, out _))
                    return false;
                status = unchecked((uint)Marshal.ReadInt32(buffer));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = ClosePrinter(handle);
        }
    }

    public IReadOnlyList<string> GetInstalledPrinterNames()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        try
        {
            return PrinterSettings.InstalledPrinters
                .Cast<string>()
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Task<PrinterProbeResult> ProbeAsync(
        string configuredPrinterName = "",
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindows())
            {
                return new PrinterProbeResult(
                    false,
                    "Star mC-Print3 Drucker ist in dieser TOR-Version nur unter Windows aktiviert.");
            }

            string? selected = null;

            // R65: checkout always knows the configured printer name. In that
            // path do NOT enumerate every Windows printer; network/offline printer
            // discovery can block for many seconds on some PCs.
            if (!string.IsNullOrWhiteSpace(configuredPrinterName))
            {
                selected = configuredPrinterName.Trim();
            }
            else
            {
                var installed = GetInstalledPrinterNames();
                if (installed.Count == 0)
                {
                    return new PrinterProbeResult(
                        false,
                        "Keine Windows-Drucker gefunden. Star Windows Software / Treiber installieren.");
                }

                selected = installed.FirstOrDefault(IsPreferredStarMcPrint3Name);
                selected ??= installed.FirstOrDefault(x =>
                    x.Contains("Star", StringComparison.OrdinalIgnoreCase));

                if (selected is null)
                {
                    return new PrinterProbeResult(
                        false,
                        "Star mC-Print3 / MCP31 Windows-Drucker nicht gefunden.");
                }
            }

            var settings = new PrinterSettings { PrinterName = selected };
            if (!settings.IsValid)
            {
                return new PrinterProbeResult(
                    false,
                    $"Windows-Drucker ist nicht gültig/erreichbar: {selected}",
                    selected,
                    SupportedModel);
            }

            if (TryReadWindowsPrinterStatus(selected, out var status) &&
                PrinterStatusProblem(status) is { } problem)
            {
                return new PrinterProbeResult(
                    false,
                    $"{problem} · {selected}",
                    selected,
                    SupportedModel);
            }

            return new PrinterProbeResult(
                true,
                $"Star mC-Print3 bereit: {selected}",
                selected,
                SupportedModel);
        }, ct);
    }

    public Task PrintTestAsync(
        string printerName,
        CancellationToken ct = default)
    {
        var job = new ReceiptPrintJob(
            ReceiptNumber: 0,
            CreatedAt: DateTimeOffset.Now,
            CompanyName: "TOR POS Pro",
            CompanyAddress: "",
            TaxNumber: "",
            VatId: "",
            Header: "DRUCKER-TEST",
            Footer: "Windows-Druckertest\nLesbarkeit und Papierformat prüfen",
            PaymentLabel: "TEST",
            DiscountCents: 0,
            TotalCents: 0,
            Lines: Array.Empty<CartLine>(),
            FiscalTestMode: true);

        return EnqueueAsync(job, null, null, printerName, isTest: true, ct);
    }

    public Task PrintReceiptAsync(
        ReceiptPrintJob job,
        string printerName,
        CancellationToken ct = default) =>
        EnqueueAsync(job, null, null, printerName, isTest: false, ct);

    public Task PrintErrorSlipAsync(
        ErrorSlipPrintJob job,
        string printerName,
        CancellationToken ct = default) =>
        EnqueueAsync(null, job, null, printerName, isTest: false, ct);

    public Task PrintReportAsync(
        ReportPrintJob job,
        string printerName,
        CancellationToken ct = default) =>
        EnqueueAsync(null, null, job, printerName, isTest: false, ct);

    public Task PrintKitchenAsync(
        KitchenPrintJob job,
        string printerName,
        CancellationToken ct = default) =>
        EnqueueAsync(null, null, null, printerName, isTest: false, ct, kitchenJob: job);

    public Task PrintPickupSlipAsync(
        PickupSlipPrintJob job,
        string printerName,
        CancellationToken ct = default) =>
        EnqueueAsync(null, null, null, printerName, isTest: false, ct, pickupSlipJob: job);

    public Task SubmitOrderAsync(PrintJobRecord record) => EnqueueAsync(null,null,null,record.Printer,false,CancellationToken.None,record.Kitchen,record.PickupSlip,record.Id);

    private async Task EnqueueAsync(
        ReceiptPrintJob? receiptJob,
        ErrorSlipPrintJob? errorJob,
        ReportPrintJob? reportJob,
        string printerName,
        bool isTest,
        CancellationToken ct,
        KitchenPrintJob? kitchenJob = null,
        PickupSlipPrintJob? pickupSlipJob = null, string? persistentId = null)
    {
        await _initialize.ConfigureAwait(false);
        if (_disposed) throw new ObjectDisposedException(nameof(StarMcPrint3PrinterService));
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Drucker wurde noch nicht ausgewählt.");

        if (_spoolerStateUncertain)
            throw new InvalidOperationException(
                "Druckerstatus nach Timeout unklar. Windows-Druckwarteschlange prüfen und TOR POS neu starten.");

        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var record=new PrintJobRecord(persistentId ?? Guid.NewGuid().ToString("N"),"QUEUED",printerName,receiptJob,errorJob,"",reportJob,kitchenJob,pickupSlipJob);
        await _printJournal.SaveAsync(record).ConfigureAwait(false);
        var item = new QueueItem(
            record,
            receiptJob,
            errorJob,
            reportJob,
            kitchenJob,
            pickupSlipJob,
            printerName.Trim(),
            isTest,
            completion,
            ct);

        if (!_queue.Writer.TryWrite(item))
        {
            await _printJournal.SaveAsync(record with {State="NOT_SUBMITTED",Note="Queue unavailable"});
            throw new InvalidOperationException("Druckwarteschlange ist voll oder geschlossen.");
        }
        await completion.Task.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                _processing=true;
                try
                {
                    item.CancellationToken.ThrowIfCancellationRequested();
                    if(_spoolerStateUncertain)
                    {
                        await _printJournal.SaveAsync(item.Record with {State="NOT_SUBMITTED",Note="Earlier job unresolved"});
                        throw new InvalidOperationException("Vorheriger Druckauftrag unklar. Warteschlange gesperrt.");
                    }

                    // R89: a live Windows printer status check right before THIS job,
                    // not just once at the initial Geräte-Manager probe - catches the
                    // printer having gone offline/out of paper since that probe,
                    // before wasting a spooler attempt on it. This outcome is
                    // definite, not ambiguous (nothing was ever sent to the printer),
                    // so unlike every failure below it must NOT set
                    // _spoolerStateUncertain - that flag exists for genuinely unknown
                    // outcomes, and locking the whole queue over routine "Papier
                    // nachlegen" would be a false alarm the cashier can't clear
                    // without an unnecessary ResolveQueueAsync review.
                    if (TryReadWindowsPrinterStatus(item.PrinterName, out var liveStatus) &&
                        PrinterStatusProblem(liveStatus) is { } liveProblem)
                    {
                        await _printJournal.SaveAsync(item.Record with {State="NOT_SUBMITTED",Note=liveProblem});
                        item.Completion.TrySetException(new InvalidOperationException(liveProblem));
                        continue;
                    }

                    await _printJournal.SaveAsync(item.Record with {State="SUBMITTED"});

                    var printTask = Task.Run(
                        async () =>
                        {
                            if(_printOverride is not null) { await _printOverride(); return; }
                            if (item.ErrorJob is not null)
                                PrintErrorSlipNow(item.ErrorJob, item.PrinterName);
                            else if (item.KitchenJob is not null)
                                PrintKitchenNow(item.KitchenJob, item.PrinterName);
                            else if (item.PickupSlipJob is not null)
                                PrintPickupSlipNow(item.PickupSlipJob, item.PrinterName);
                            else if (item.ReportJob is not null)
                                PrintReportNow(item.ReportJob, item.PrinterName);
                            else if (item.ReceiptJob is not null)
                                PrintNow(item.ReceiptJob, item.PrinterName, item.IsTest);
                            else
                                throw new InvalidOperationException("Leerer Druckauftrag.");
                        },
                        CancellationToken.None);

                    _activePrint=printTask;
                    var timeoutTask = Task.Delay(PrintTimeout, _shutdown.Token);
                    var completed = await Task.WhenAny(printTask, timeoutTask);

                    if (completed != printTask)
                    {
                        _spoolerStateUncertain = true;
                        await _printJournal.SaveAsync(item.Record with {State="UNKNOWN",Note="Timeout/shutdown. May still print."});
                        _ = printTask.ContinueWith(
                            t => _ = t.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted |
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        item.Completion.TrySetException(
                            new TimeoutException(
                                $"Drucker antwortet länger als {PrintTimeout.TotalSeconds:0} Sekunden nicht. " +
                                "Druckstatus ist unklar; Bon nicht blind erneut drucken."));
                        continue;
                    }

                    await printTask.ConfigureAwait(false);
                    await _printJournal.SaveAsync(item.Record with {State="SPOOL_ACCEPTED",Note="Driver returned; physical print not verified"});
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException oce)
                {
                    item.Completion.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    _spoolerStateUncertain=true;
                    item.Completion.TrySetException(ex);
                }
                finally { _processing=false; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while(_queue.Reader.TryRead(out var pending))
                pending.Completion.TrySetException(new InvalidOperationException("Programm beendet; Druckauftrag zur Prüfung gespeichert."));
        }
    }

    private static bool IsPreferredStarMcPrint3Name(string name) =>
        name.Contains("MCP31", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MCP30", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("mC-Print3", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("mC Print3", StringComparison.OrdinalIgnoreCase);

    private static void PrintNow(
        ReceiptPrintJob job,
        string printerName,
        bool isTest)
    {
        if (!isTest && !job.FiscalTestMode)
            ValidateFiscalReceipt(job);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows printer driver required.");

        using var document = new PrintDocument();
        document.DocumentName = isTest
            ? "TOR POS - Star mC-Print3 Test"
            : $"TOR POS Bon {job.ReceiptNumber:000000}";

        document.PrinterSettings = new PrinterSettings
        {
            PrinterName = printerName
        };

        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException(
                $"Drucker nicht verfügbar: {printerName}");

        document.PrintController = new StandardPrintController();
        document.DefaultPageSettings.Margins = new Margins(4, 4, 4, 4);

        // 80 mm roll. Driver may override this with its configured receipt size.
        document.DefaultPageSettings.PaperSize =
            new PaperSize("80mm Receipt", 315, 1200);

        document.PrintPage += (_, e) =>
        {
            var graphics = e.Graphics;

            if (graphics is null)
            {
                throw new InvalidOperationException(
                    "Drucker konnte keinen Graphics-Kontext bereitstellen.");
            }

            DrawReceipt(
                graphics,
                e.MarginBounds,
                job,
                isTest);

            e.HasMorePages = false;
        };

        document.Print();
        SendRawCommandsAfterPrint(printerName, job.AutoCut, job.OpenCashDrawer);
    }

    /// <summary>
    /// Cut/drawer-kick are sent as a separate RAW spooler job right after the
    /// GDI receipt print, so a printer that doesn't understand (or ignores)
    /// these bytes never blocks the receipt itself. A failure here must not
    /// surface as a print failure - the fiscal/paper duty (the receipt) is
    /// already done; losing an automatic cut or drawer kick is a hardware
    /// convenience issue, not a reason to make the cashier believe printing
    /// failed and retry.
    /// </summary>
    private static void SendRawCommandsAfterPrint(string printerName, bool autoCut, bool openCashDrawer)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (autoCut)
        {
            try { RawPrinterIo.SendRaw(printerName, StarPrntRawCommands.PartialCut, "TOR POS - Schnitt"); }
            catch { /* see summary above */ }
        }

        if (openCashDrawer)
        {
            try { RawPrinterIo.SendRaw(printerName, StarPrntRawCommands.OpenCashDrawer(), "TOR POS - Kassenschublade"); }
            catch { /* see summary above */ }
        }
    }

    private static void PrintErrorSlipNow(
        ErrorSlipPrintJob job,
        string printerName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows printer driver required.");

        using var document = new PrintDocument();
        document.DocumentName = $"TOR POS Fehler {job.ErrorId}";
        document.PrinterSettings = new PrinterSettings { PrinterName = printerName };

        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Drucker nicht verfügbar: {printerName}");

        document.PrintController = new StandardPrintController();
        document.DefaultPageSettings.Margins = new Margins(4, 4, 4, 4);
        document.DefaultPageSettings.PaperSize = new PaperSize("80mm Receipt", 315, 900);

        document.PrintPage += (_, e) =>
        {
            var g = e.Graphics;
            if (g is null)
                throw new InvalidOperationException(
                    "Drucker konnte keinen Graphics-Kontext bereitstellen.");

            g.PageUnit = GraphicsUnit.Display;
            using var normal = new Font("Arial", 8.5f, FontStyle.Regular);
            using var bold = new Font("Arial", 9.5f, FontStyle.Bold);
            using var title = new Font("Arial", 12f, FontStyle.Bold);

            var y = (float)e.MarginBounds.Top;
            var left = (float)e.MarginBounds.Left;
            var width = (float)e.MarginBounds.Width;
            const float line = 15f;

            void Center(string text, Font font)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, Brushes.Black,
                    left + Math.Max(0, (width - size.Width) / 2), y);
                y += Math.Max(line, size.Height) + 2;
            }

            void Text(string text, Font font)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                g.DrawString(text, font, Brushes.Black,
                    new RectangleF(left, y, width, 240),
                    new StringFormat
                    {
                        Trimming = StringTrimming.Word,
                        FormatFlags = StringFormatFlags.LineLimit
                    });
                var h = g.MeasureString(text, font, (int)width).Height;
                y += Math.Max(line, h) + 2;
            }

            void Rule()
            {
                y += 2;
                g.DrawLine(Pens.Black, left, y, left + width, y);
                y += 6;
            }

            Center("TOR POS FEHLERPROTOKOLL", title);
            Center("KEIN STEUERBELEG", normal);
            Rule();
            Text($"Datum: {job.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}", normal);
            Text($"Kasse: {job.CashRegister}", normal);
            if (!string.IsNullOrWhiteSpace(job.OperatorName))
                Text($"Bediener: {job.OperatorName}", normal);
            Rule();
            Text($"FEHLER: {job.Category}", bold);
            Text(job.Message, normal);
            Rule();
            Text($"Fehler-ID: {job.ErrorId}", bold);
            Text("Details im TOR POS Log", normal);
            Text(@"%LOCALAPPDATA%\TOR POS Pro\Logs", normal);
            Rule();
            Center("Bitte Fehler-ID für Service aufbewahren", normal);
            e.HasMorePages = false;
        };

        document.Print();
        SendRawCommandsAfterPrint(printerName, job.AutoCut, false);
    }

    private static void PrintReportNow(
        ReportPrintJob job,
        string printerName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows printer driver required.");

        using var document = new PrintDocument();
        document.DocumentName = $"TOR POS Bericht - {job.Title}";
        document.PrinterSettings = new PrinterSettings { PrinterName = printerName };
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Drucker nicht verfügbar: {printerName}");

        document.PrintController = new StandardPrintController();
        if (job.PaperFormat == ReportPaperFormat.A4)
        {
            document.DefaultPageSettings.Margins = new Margins(35, 35, 35, 35);
            document.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169);
        }
        else if (job.PaperFormat == ReportPaperFormat.Receipt58)
        {
            document.DefaultPageSettings.Margins = new Margins(3, 3, 4, 4);
            document.DefaultPageSettings.PaperSize = new PaperSize("58mm Receipt", 228, 1800);
        }
        else
        {
            document.DefaultPageSettings.Margins = new Margins(4, 4, 4, 4);
            document.DefaultPageSettings.PaperSize = new PaperSize("80mm Receipt", 315, 1800);
        }

        var lineIndex = 0;
        var lineOffset = 0;
        var pageNumber = 0;
        document.PrintPage += (_, e) =>
        {
            var g = e.Graphics ?? throw new InvalidOperationException(
                "Drucker konnte keinen Graphics-Kontext bereitstellen.");
            g.PageUnit = GraphicsUnit.Display;

            var receipt = job.PaperFormat != ReportPaperFormat.A4;
            using var normal = new Font("Arial", receipt ? 7.8f : 9.2f, FontStyle.Regular);
            using var bold = new Font("Arial", receipt ? 9.0f : 10.5f, FontStyle.Bold);
            using var title = new Font("Arial", receipt ? 11.0f : 14.0f, FontStyle.Bold);
            using var small = new Font("Arial", receipt ? 7.0f : 8.0f, FontStyle.Regular);

            pageNumber++;
            var left = (float)e.MarginBounds.Left;
            var top = (float)e.MarginBounds.Top;
            var width = (float)e.MarginBounds.Width;
            var bottom = (float)e.MarginBounds.Bottom;
            var y = top;

            if (pageNumber == 1)
            {
                var titleSize = g.MeasureString(job.Title, title, (int)Math.Max(40, width));
                g.DrawString(job.Title, title, Brushes.Black,
                    new RectangleF(left, y, width, titleSize.Height + 4));
                y += titleSize.Height + 6;
                g.DrawString($"Erstellt: {job.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}", small, Brushes.Black, left, y);
                y += g.MeasureString("Ag", small).Height + 6;
                g.DrawLine(Pens.Black, left, y, left + width, y);
                y += 7;
            }
            else
            {
                g.DrawString($"{job.Title} · Seite {pageNumber}", small, Brushes.Black, left, y);
                y += g.MeasureString("Ag", small).Height + 6;
            }

            var startIndex=lineIndex;
            var startOffset=lineOffset;
            while (lineIndex < job.Lines.Count)
            {
                var line = job.Lines[lineIndex] ?? "";
                var font = line.Length > 0 && !line.Contains('|') && line == line.ToUpperInvariant() ? bold : normal;
                var remaining=line.Length==0?" ":line[lineOffset..];
                var height=bottom-18-y-2;
                if(height<font.GetHeight(g))break;
                using var format=new StringFormat(StringFormat.GenericDefault){FormatFlags=StringFormatFlags.LineLimit,Trimming=StringTrimming.None};
                var size=g.MeasureString(remaining,font,new SizeF(Math.Max(1,width),height),format,out var fitted,out var fittedLines);
                if(fitted<=0)break;
                var part=remaining[..fitted];
                g.DrawString(part,font,Brushes.Black,new RectangleF(left,y,width,height),format);
                y+=Math.Max(receipt?13f:16f,size.Height)+2;
                if(line.Length==0 || lineOffset+fitted>=line.Length){lineIndex++;lineOffset=0;}
                else {lineOffset+=fitted;break;}
            }
            if(lineIndex<job.Lines.Count && lineIndex==startIndex && lineOffset==startOffset)
                throw new InvalidOperationException("Kein druckbarer Platz für Berichtstext. Papierformat/Ränder prüfen.");

            g.DrawString($"TOR POS · {pageNumber}", small, Brushes.Black,
                left, Math.Max(y + 4, bottom - 12));
            e.HasMorePages = lineIndex < job.Lines.Count;
        };

        document.Print();
        SendRawCommandsAfterPrint(printerName, job.AutoCut, false);
    }

    private static void DrawReceipt(
        Graphics g,
        Rectangle bounds,
        ReceiptPrintJob job,
        bool isTest)
    {
        g.PageUnit = GraphicsUnit.Display;

        using var normal = new Font("Arial", 8.5f, FontStyle.Regular);
        using var bold = new Font("Arial", 9.5f, FontStyle.Bold);
        using var title = new Font("Arial", 12f, FontStyle.Bold);
        using var pickup = new Font("Arial", 24f, FontStyle.Bold);
        using var small = new Font("Arial", 7.5f, FontStyle.Regular);

        var y = (float)bounds.Top;
        var left = (float)bounds.Left;
        var width = (float)bounds.Width;
        var line = 15f;

        void Center(string text, Font font, float extra = 0)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.Black, left + Math.Max(0, (width - size.Width) / 2), y);
            y += Math.Max(line, size.Height) + extra;
        }

        void Text(string text, Font font, float extra = 0)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            g.DrawString(
                text,
                font,
                Brushes.Black,
                new RectangleF(left, y, width, 200),
                new StringFormat { Trimming = StringTrimming.Word, FormatFlags = StringFormatFlags.LineLimit });
            var h = g.MeasureString(text, font, (int)width).Height;
            y += Math.Max(line, h) + extra;
        }

        void Rule()
        {
            y += 2;
            g.DrawLine(Pens.Black, left, y, left + width, y);
            y += 5;
        }

        void Logo(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                using var image = Image.FromFile(path);
                var maxWidth = width * 0.72f;
                var maxHeight = 90f;
                var scale = Math.Min(maxWidth / image.Width, maxHeight / image.Height);
                if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) return;
                var drawWidth = Math.Max(1f, image.Width * scale);
                var drawHeight = Math.Max(1f, image.Height * scale);
                var x = left + Math.Max(0, (width - drawWidth) / 2);
                g.DrawImage(image, x, y, drawWidth, drawHeight);
                y += drawHeight + 6;
            }
            catch
            {
                // Ein defektes/verschobenes Logo darf niemals den Bon-Druck blockieren.
            }
        }

        bool QrCode(string payload, float maxSize = 130f)
        {
            if (string.IsNullOrWhiteSpace(payload)) return false;
            try
            {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
                using var qr = new QRCoder.QRCode(data);
                using var bitmap = qr.GetGraphic(6);
                var scale = Math.Min(maxSize / bitmap.Width, maxSize / bitmap.Height);
                if (scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) return false;
                var drawWidth = Math.Max(1f, bitmap.Width * scale);
                var drawHeight = Math.Max(1f, bitmap.Height * scale);
                var x = left + Math.Max(0, (width - drawWidth) / 2);
                g.DrawImage(bitmap, x, y, drawWidth, drawHeight);
                y += drawHeight + 4;
                return true;
            }
            catch
            {
                // Caller falls back to printing the five TSE fields as text -
                // the fiscal data must reach the receipt one way or the other,
                // a QR rendering failure is not a reason to omit it.
                return false;
            }
        }

        Logo(job.LogoPath);
        Center(job.CompanyName, title);
        if (!string.IsNullOrWhiteSpace(job.CompanyAddress))
            Center(job.CompanyAddress, normal);

        if (!string.IsNullOrWhiteSpace(job.Header))
        {
            Rule();
            Text(job.Header, normal);
        }

        Rule();

        if (isTest)
        {
            Center("STAR mC-Print3", bold);
            Center("MCP31CBI / 80 mm", normal);
            Center(DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"), normal);
            Rule();
            Center("DRUCKTEST OK", bold);
            Text("Umlaute: Ä Ö Ü · ä ö ü · ß · €", normal);
        }
        else
        {
            Text(job.FiscalTestMode && job.ReceiptNumber == 0 ? "TESTBELEG - OHNE FISKALE BONNUMMER" : $"Bon: {job.ReceiptNumber:000000}", bold);
            Text($"Datum: {job.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}", normal);
            if (!string.IsNullOrWhiteSpace(job.OperatorName))
                Text($"Bediener: {job.OperatorName}", normal);
            if (job.PickupNumber > 0)
            {
                Rule();
                Center("ABHOLNUMMER", bold);
                Center(job.PickupNumber.ToString("000"), pickup, 3);
            }
            Rule();

            foreach (var item in job.Lines)
            {
                var name = item.ProductName;
                if (!string.IsNullOrWhiteSpace(item.VariantName))
                    name += $" · {item.VariantName}";

                Text(name, bold);

                // R123: "1,5 x" on a German Beleg, whatever the Windows culture.
                var qty = GermanFormat.Number(item.Quantity, "0.##");

                if (item.HasPromotion)
                {
                    Text(
                        $"{qty} x {Money(item.EffectiveListUnitPriceCents)}  →  " +
                        $"{Money(item.UnitPriceCents)}     {Money(item.LineTotalCents)}",
                        normal);

                    Text(
                        $"ANGEBOT -{item.PromotionPercent}% · " +
                        $"-{Money(item.PromotionDiscountCents)}",
                        small);
                }
                else
                {
                    var unit = Money(item.UnitPriceCents);
                    var total = Money(item.LineTotalCents);
                    Text($"{qty} x {unit}     {total}", normal);
                }

                if (item.PfandCents > 0)
                    Text($"inkl. Pfand {Money(item.PfandCents)} · nicht rabattiert", small);
            }

            var promotions = job.Lines
                .Where(x => x.HasPromotion)
                .GroupBy(x => new
                {
                    x.PromotionId,
                    x.PromotionName,
                    x.PromotionPercent,
                    x.PromotionStartDate,
                    x.PromotionEndDate
                })
                .Select(g => new
                {
                    g.Key.PromotionName,
                    g.Key.PromotionPercent,
                    g.Key.PromotionStartDate,
                    g.Key.PromotionEndDate,
                    DiscountCents = g.Sum(x => x.PromotionDiscountCents)
                })
                .ToArray();

            if (promotions.Length > 0)
            {
                Rule();
                Text("ANGEBOTE / AKTIONEN", bold);

                static string PromotionDate(string value)
                {
                    return DateOnly.TryParseExact(
                        value,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var day)
                            ? day.ToString("dd.MM.yyyy")
                            : value;
                }

                foreach (var promotion in promotions)
                {
                    Text(
                        $"{promotion.PromotionName} · -{promotion.PromotionPercent}%",
                        normal);
                    Text(
                        $"Gültig: {PromotionDate(promotion.PromotionStartDate)} - " +
                        $"{PromotionDate(promotion.PromotionEndDate)} · " +
                        $"Rabatt {Money(promotion.DiscountCents)}",
                        small);
                }
            }

            Rule();

            var promotionDiscount = job.Lines.Sum(x => x.PromotionDiscountCents);
            if (promotionDiscount > 0)
                Text($"Angebote gesamt: -{Money(promotionDiscount)}", normal);

            if (job.DiscountCents > 0)
                Text($"Manueller Rabatt: -{Money(job.DiscountCents)}", normal);

            Text($"GESAMT: {Money(job.TotalCents)}", title);
            Text($"Zahlart: {job.PaymentLabel}", normal);
            if (job.TenderedCents > 0)
            {
                Text($"Gegeben: {Money(job.TenderedCents)}", normal);
                Text($"Rückgeld: {Money(job.ChangeCents)}", normal);
            }

            if (!string.IsNullOrWhiteSpace(job.TaxNumber))
                Text($"St.-Nr.: {job.TaxNumber}", small);

            if (!string.IsNullOrWhiteSpace(job.VatId))
                Text($"USt-IdNr.: {job.VatId}", small);

            Rule();
            foreach (var tax in BuildTaxSummary(job))
            {
                Text(
                    GermanFormat.Line($"MwSt {tax.Rate:0.##}% · Brutto {Money(tax.GrossCents)} · MwSt {Money(tax.TaxCents)}"),
                    small);
            }

            if (job.FiscalTestMode)
            {
                Rule();
                Center("TESTBON - KEIN STEUERBELEG", bold);
                Center("NICHT FUER PRODUKTIVBETRIEB", bold);
                Center("TSE / DSFinV-K NICHT FREIGEGEBEN", small);
                if (!string.IsNullOrWhiteSpace(job.EasSerial))
                    Text($"eAS-Test-ID: {job.EasSerial}", small);
            }
            else
            {
                Rule();
                if (!job.TseQrCode || !QrCode(TseQrCodePayload.Build(job)))
                {
                    Text($"eAS: {job.EasSerial}", small);
                    Text($"TSE: {job.TseSerial}", small);
                    Text($"Transaktion: {job.TseTransactionNumber}", small);
                    Text($"Signaturzaehler: {job.SignatureCounter}", small);
                    Text($"Pruefwert: {job.VerificationValue}", small);
                }

                // R136: § 6 Satz 1 Nr. 3 KassenSichV - Vorgangsbeginn and
                // Vorgangsende, also next to the QR code, which does not carry them.
                if (job.ProcessStart is { } processStart)
                    Text($"Vorgangsbeginn: {processStart.LocalDateTime:dd.MM.yyyy HH:mm:ss}", small);
                if (job.ProcessEnd is { } processEnd)
                    Text($"Vorgangsende: {processEnd.LocalDateTime:dd.MM.yyyy HH:mm:ss}", small);
                // R137: DSFinV-K 2.7.2 - a receipt for an order shows when the
                // first order transaction started.
                if (job.OrderStart is { } orderStart)
                    Text($"Bestellbeginn: {orderStart.LocalDateTime:dd.MM.yyyy HH:mm:ss}", small);

                if (job.TseOutage)
                {
                    Rule();
                    Center("TSE-AUSFALL", bold);
                    Center("Vorgang ohne TSE-Signatur", small);
                    Center("Ausfall ist im System protokolliert", small);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(job.Footer))
        {
            Rule();
            Text(job.Footer, normal);
        }

        y += 20;
    }


    private static void PrintKitchenNow(KitchenPrintJob job, string printerName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows printer driver required.");
        using var document=new PrintDocument();
        document.DocumentName=$"TOR POS Küchenbon {job.PickupNumber:000}";
        document.PrinterSettings=new PrinterSettings { PrinterName=printerName };
        if(!document.PrinterSettings.IsValid) throw new InvalidOperationException($"Drucker nicht verfügbar: {printerName}");
        document.PrintController=new StandardPrintController(); document.DefaultPageSettings.Margins=new Margins(4,4,4,4);
        document.DefaultPageSettings.PaperSize=new PaperSize("80mm Receipt",315,1200);
        var entries = new List<(string Text,bool Bold)>();
        // The instruction precedes products so a cancellation cannot look like a new order.
        if(!string.IsNullOrWhiteSpace(job.Note)) entries.Add(("HINWEIS: "+job.Note,true));
        if(!string.IsNullOrWhiteSpace(job.OperatorName)) entries.Add(("Bediener: "+job.OperatorName,false));
        entries.AddRange(job.Lines.Select(line=>((line.IsComponent?"   + ":"")+GermanFormat.Line($"{line.Quantity:0.##} x {line.Name}"),!line.IsComponent)));
        entries.Add(("KEIN STEUERBELEG",false));
        int index=0,offset=0,page=0;
        document.PrintPage+=(_,e)=>{
            var g=e.Graphics??throw new InvalidOperationException("Drucker konnte keinen Graphics-Kontext bereitstellen.");
            g.PageUnit=GraphicsUnit.Display;
            using var normal=new Font("Arial",10f);using var bold=new Font("Arial",12f,FontStyle.Bold);
            using var big=new Font("Arial",26f,FontStyle.Bold);using var small=new Font("Arial",8f);
            float left=e.MarginBounds.Left,width=e.MarginBounds.Width,y=e.MarginBounds.Top,bottom=e.MarginBounds.Bottom;
            void Header(string text,Font font){var size=g.MeasureString(text,font,(int)Math.Max(1,width));g.DrawString(text,font,Brushes.Black,new RectangleF(left,y,width,size.Height+3));y+=size.Height+4;}
            page++;Header("KÜCHENBON · BESTELLUNG",bold);
            if(job.PickupNumber>0)Header($"ABHOLNR. {job.PickupNumber:000}",big);
            Header($"P{job.ParkNumber:000000} · Seite {page}",small);
            Header(job.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),small);
            var startIndex=index;var startOffset=offset;
            while(index<entries.Count){
                var entry=entries[index];var font=entry.Bold?bold:normal;
                var remaining=entry.Text.Length==0?" ":entry.Text[offset..];var height=bottom-y-18;
                if(height<font.GetHeight(g))break;
                using var format=new StringFormat(StringFormat.GenericDefault){FormatFlags=StringFormatFlags.LineLimit,Trimming=StringTrimming.None};
                var size=g.MeasureString(remaining,font,new SizeF(Math.Max(1,width),height),format,out var fitted,out var fittedLines);
                if(fitted<=0)break;
                g.DrawString(remaining[..fitted],font,Brushes.Black,new RectangleF(left,y,width,height),format);y+=size.Height+4;
                if(entry.Text.Length==0||offset+fitted>=entry.Text.Length){index++;offset=0;}else{offset+=fitted;break;}
            }
            if(index<entries.Count&&startIndex==index&&startOffset==offset)throw new InvalidOperationException("Kein Platz für Küchenbon. Papierformat/Ränder prüfen.");
            e.HasMorePages=index<entries.Count;
            if(e.HasMorePages)g.DrawString("FORTSETZUNG AUF NÄCHSTEM BON",small,Brushes.Black,left,bottom-14);
        };
        document.Print();
        SendRawCommandsAfterPrint(printerName, job.AutoCut, false);
    }

    private static void PrintPickupSlipNow(PickupSlipPrintJob job, string printerName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows printer driver required.");
        using var document=new PrintDocument(); document.DocumentName=$"TOR POS Abholschein {job.PickupNumber:000}";
        document.PrinterSettings=new PrinterSettings { PrinterName=printerName };
        if(!document.PrinterSettings.IsValid) throw new InvalidOperationException($"Drucker nicht verfügbar: {printerName}");
        document.PrintController=new StandardPrintController();document.DefaultPageSettings.Margins=new Margins(4,4,4,4);document.DefaultPageSettings.PaperSize=new PaperSize("80mm Receipt",315,500);
        document.PrintPage+=(_,e)=>{var g=e.Graphics??throw new InvalidOperationException("Drucker konnte keinen Graphics-Kontext bereitstellen.");using var title=new Font("Arial",13f,FontStyle.Bold);using var big=new Font("Arial",34f,FontStyle.Bold);using var small=new Font("Arial",8f);float y=e.MarginBounds.Top,left=e.MarginBounds.Left,width=e.MarginBounds.Width;void C(string t,Font f){var z=g.MeasureString(t,f);g.DrawString(t,f,Brushes.Black,left+Math.Max(0,(width-z.Width)/2),y);y+=Math.Max(18,z.Height)+4;}if(!string.IsNullOrWhiteSpace(job.CompanyName))C(job.CompanyName,title);C("ABHOLSCHEIN",title);C(job.PickupNumber.ToString("000"),big);C($"Bestellung P{job.ParkNumber:000000}",small);C(job.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),small);C("KEIN STEUERBELEG",small);e.HasMorePages=false;};
        document.Print();
        SendRawCommandsAfterPrint(printerName, job.AutoCut, false);
    }


private static void ValidateFiscalReceipt(ReceiptPrintJob job)
{
    // R122: the list itself now lives in TorPos.Core.FiscalReceiptFields, so
    // the digital receipt (R103) checks the SAME fields instead of silently
    // omitting whichever ones happened to be blank. R121's rule is unchanged:
    // Vorgangsende comes from the TSE log time and belongs with the other
    // TSE-generated fields - mandatory when signed, legitimately absent during
    // an outage.
    var missing = FiscalReceiptFields.Missing(
        job.CompanyName,
        job.CompanyAddress,
        job.EasSerial,
        job.TseOutage,
        job.TseSerial,
        job.TseTransactionNumber,
        job.SignatureCounter > 0,
        job.VerificationValue,
        job.ProcessStart is not null,
        job.ProcessEnd is not null);

    if (missing.Count > 0)
    {
        throw new InvalidOperationException(
            "Produktivbeleg gesperrt. Fiskal-Felder fehlen: " +
            string.Join(", ", missing));
    }
}

// R106: delegates to the shared TorPos.Core.VatSummaryCalculator instead
// of its own copy of this formula - the digital receipt (R103) had
// hand-duplicated this and computed VAT on the pre-discount gross,
// overstating it whenever a manual discount applied. One shared
// implementation now, so the two can no longer silently diverge again.
private static IReadOnlyList<TaxSummary> BuildTaxSummary(ReceiptPrintJob job) =>
    VatSummaryCalculator.Compute(job.Lines, job.DiscountCents)
        .Select(x => new TaxSummary(x.Rate, x.GrossCents, x.TaxCents))
        .ToArray();

private sealed record TaxSummary(
    decimal Rate,
    long GrossCents,
    long TaxCents);

    // R123: this formatted with the Windows account's culture, so on an
    // English Windows the Kassenbon printed "5.00 EUR". Same bug PR #1 found
    // in the STORNO report, on the one document where it matters most.
    private static string Money(long cents) => GermanFormat.Eur(cents);

    public async ValueTask DisposeAsync()
    {
        _disposed=true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Shutdown should never block application exit.
        }

        // The timed-out native task may still reference shutdown resources.
        if (_worker.IsCompleted) _shutdown.Dispose();
    }

    public Task<IReadOnlyList<PrintJobRecord>> GetUncertainJobsAsync() => _printJournal.GetUncertainAsync();

    public async Task ResolveQueueAsync(string evidence)
    {
        await _initialize.ConfigureAwait(false);
        if (_processing || _queue.Reader.Count>0 || _activePrint is {IsCompleted:false}) throw new InvalidOperationException("Treiberaufruf läuft noch. Windows-Druckwarteschlange prüfen und TOR neu starten.");
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length<8) throw new InvalidOperationException("Prüfnachweis fehlt.");
        foreach(var job in await _printJournal.GetUncertainAsync())
            await _printJournal.SaveAsync(job with {State="REVIEWED",Note=evidence});
        _spoolerStateUncertain=false;
    }

    private sealed record QueueItem(
        PrintJobRecord Record,
        ReceiptPrintJob? ReceiptJob,
        ErrorSlipPrintJob? ErrorJob,
        ReportPrintJob? ReportJob,
        KitchenPrintJob? KitchenJob,
        PickupSlipPrintJob? PickupSlipJob,
        string PrinterName,
        bool IsTest,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken);
}
