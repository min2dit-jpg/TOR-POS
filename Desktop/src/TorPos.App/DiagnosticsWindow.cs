using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class DiagnosticsWindow : Window
{
    private readonly PerformanceCounters _performance;
    private readonly ISettingsRepository _settings;
    private readonly IReceiptPrinterService _printer;
    private readonly IPaymentTerminalService _terminal;
    private readonly ITseProvider _tse;
    private readonly SchemaMigrationService _schemaMigrations;
    private readonly ControlledPosActionService _controlledActions;
    private readonly PromotionCampaignService _promotions;
    private readonly AuthenticationService _authentication;
    private readonly ICheckoutJournal _checkoutJournal;
    private readonly DatabaseHealthService _databaseHealth;
    private readonly ICardRefundLockRepository _cardRefundLocks;
    private readonly StackPanel _cardRefundResults = new() { Spacing = 8 };

    // R119 follow-up: kitchen tickets the print queue gave up on. Without a
    // view here they were only queryable in the database, so a ticket that
    // never reached the kitchen stayed invisible to the operator.
    private readonly OrderPrintOutbox _orderPrints;
    private readonly StackPanel _orderPrintResults = new() { Spacing = 8 };

    private readonly StackPanel _deviceResults = new() { Spacing = 10 };
    private readonly StackPanel _performanceResults = new() { Spacing = 6 };
    private readonly StackPanel _recentResults = new() { Spacing = 4 };
    private readonly TextBlock _status = new()
    {
        Text = "Noch nicht geprüft.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.TextMuted
    };
    private readonly Button _runButton = new()
    {
        Content = "ALLE GERÄTE PRÜFEN",
        MinHeight = 48,
        MinWidth = 210
    };

    public DiagnosticsWindow(
        PerformanceCounters performance,
        ISettingsRepository settings,
        IReceiptPrinterService printer,
        IPaymentTerminalService terminal,
        ITseProvider tse,
        SchemaMigrationService schemaMigrations,
        ControlledPosActionService controlledActions,
        PromotionCampaignService promotions,
        AuthenticationService authentication,
        ICheckoutJournal checkoutJournal,
        DatabaseHealthService databaseHealth,
        ICardRefundLockRepository cardRefundLocks,
        OrderPrintOutbox orderPrints)
    {
        _orderPrints = orderPrints;
        _performance = performance;
        _settings = settings;
        _printer = printer;
        _terminal = terminal;
        _tse = tse;
        _schemaMigrations = schemaMigrations;
        _controlledActions = controlledActions;
        _cardRefundLocks = cardRefundLocks;
        _promotions = promotions;
        _authentication = authentication;
        _checkoutJournal = checkoutJournal;
        _databaseHealth = databaseHealth;

        Title = "TOR POS · Systemstatus / Diagnose";
        Width = 980;
        Height = 760;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var refreshPerformance = new Button
        {
            Content = "PERFORMANCE AKTUALISIEREN",
            MinHeight = 48,
            MinWidth = 220
        };
        var resetPerformance = new Button
        {
            Content = "MESSWERTE ZURÜCKSETZEN",
            MinHeight = 48,
            MinWidth = 210
        };
        var openLogs = new Button
        {
            Content = "LOG-ORDNER ÖFFNEN",
            MinHeight = 48,
            MinWidth = 180
        };
        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinHeight = 48,
            MinWidth = 150
        };

        _runButton.Click += async (_, _) => await RunAllAsync();
        refreshPerformance.Click += (_, _) => RefreshPerformance();
        resetPerformance.Click += (_, _) =>
        {
            _performance.Reset();
            RefreshPerformance();
            _status.Text = "Performance-Messwerte zurückgesetzt.";
        };
        openLogs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(CrashLog.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{CrashLog.LogDirectory}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _status.Text = "Log-Ordner konnte nicht geöffnet werden: " + ex.Message;
            }
        };
        close.Click += (_, _) => Close();

        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "SYSTEMSTATUS / DIAGNOSE",
                    FontSize = 28,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                new TextBlock
                {
                    Text = "Geräte werden nur auf Anforderung geprüft. Jeder externe Test ist zeitlich begrenzt und darf die Kassenoberfläche nicht lange blockieren.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#94AABD"))
                }
            }
        };

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            Children =
            {
                _runButton,
                refreshPerformance,
                resetPerformance,
                openLogs,
                close
            }
        };

        var deviceSection = Card(
            "GERÄTESTATUS",
            "Bondrucker · Kartenterminal · Zahlungsjournal · TSE · Datenbank-Schema · SQLite/WAL · Audit-Integrität · Angebote · Datenspeicher · Backup",
            _deviceResults);

        var performanceSection = Card(
            "PERFORMANCE",
            $"Letzte/Ø/Max-Zeit. Ab {PerformanceCounters.SlowThresholdMs:0} ms wird ein technischer Vorgang als langsam markiert. Bediener-Wartezeit in Dialogen wird nicht mitgemessen.",
            _performanceResults);

        var recentSection = Card(
            "LETZTE MESSUNGEN",
            "Die letzten Messpunkte dieser Programmsitzung. Langsame Vorgänge werden hervorgehoben.",
            _recentResults);

        // R85: die Kassenoberfläche zeigt Kassierern/Kunden nur noch eine kurze
        // Statuszeile mit Fehler-ID (kein Rohtext einer .NET-Exception mehr).
        // Diese Suche ist der "Techniker-Details"-Gegenpart: die volle
        // technische Meldung zu genau dieser ID nachschlagen, ohne Log-Dateien
        // von Hand zu durchsuchen.
        var errorIdBox = new TextBox { PlaceholderText = "z. B. 20260914-153000-AB12", MinWidth = 260, MinHeight = 42 };
        var errorSearch = new Button { Content = "SUCHEN", MinHeight = 42, MinWidth = 110 };
        var errorResult = new TextBlock
        {
            Text = "Fehler-ID eingeben, die einem Kassierer/Kunden angezeigt wurde.",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            Foreground = AppTheme.TextMuted
        };
        errorSearch.Click += (_, _) =>
        {
            var found = CrashLog.FindErrorId(errorIdBox.Text ?? "");
            errorResult.Text = found ?? "Keine Fehler-ID gefunden. Genaue Schreibweise prüfen; ältere Sitzungen werden ggf. bereits automatisch bereinigt.";
            errorResult.Foreground = found is null
                ? AppTheme.TextMuted
                : new SolidColorBrush(Color.Parse("#E9F4FF"));
        };
        errorIdBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) errorSearch.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); };

        var errorLookupSection = Card(
            "TECHNIKER-DETAILS ZU EINER FEHLER-ID",
            "Kassierer sehen nur eine kurze Meldung mit Fehler-ID. Die vollständige technische Meldung (Ausnahmetyp, Text, Stacktrace) steht hier.",
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { errorIdBox, errorSearch } },
                    errorResult
                }
            });

        // R106: ICardRefundLockRepository's own doc comment explains why
        // this lock exists - an ambiguous (terminal "Status unklar") card
        // refund keeps a Bon locked against any further BON STORNO/
        // Teilretoure attempt until a technician manually verifies the
        // real-world outcome with the terminal/bank and clears it here.
        var cardRefundSection = Card(
            "BLOCKIERTE KARTENERSTATTUNGEN",
            "Ein Bon erscheint hier, wenn eine Kartenerstattung (BON STORNO/Teilretoure) mit unklarem Terminalstatus endete. Zuerst beim Kartenterminal/der Bank prüfen, ob die Erstattung tatsächlich erfolgt ist, dann hier als geklärt markieren - erst danach kann für diesen Bon erneut storniert/retourniert werden.",
            _cardRefundResults);
        _ = RefreshCardRefundLocksAsync();

        // R119 follow-up: same idea as the card-refund section above, for the
        // other queue that can silently stop delivering - kitchen tickets.
        var orderPrintSection = Card(
            "BLOCKIERTE KÜCHENBONS",
            "Ein Bestelldruck erscheint hier, wenn er nach mehreren Versuchen aufgegeben wurde - meist, weil der eingestellte Drucker nicht mehr erreichbar ist. Die Warteschlange läuft trotzdem weiter, damit spätere Bestellungen die Küche erreichen. Drucker prüfen, dann hier erneut in die Warteschlange stellen.",
            _orderPrintResults);
        _ = RefreshOrderPrintFailuresAsync();

        // BAR TESTBON preparation is intentionally pure: it does not create a
        // checkout-journal row and does not touch the TSE. A real TSE test must
        // later persist the acceptance operation and its TSE evidence together,
        // otherwise the hardware would contain an orphan transaction.
        BarTestBonPlan? barTestPlan = null;
        var barTestVat = new ComboBox
        {
            ItemsSource = new[] { "19 %", "7 %" },
            SelectedIndex = 0,
            MinWidth = 110,
            MinHeight = 42
        };
        var barTestPrepare = new Button
        {
            Content = "BAR TESTBON VORBEREITEN",
            MinHeight = 42,
            MinWidth = 220
        };
        var barTestPrint = new Button
        {
            Content = "80 MM TESTBON DRUCKEN",
            MinHeight = 42,
            MinWidth = 210
        };
        var barTestResult = new TextBlock
        {
            Text = "Noch nicht vorbereitet. Dieser Bereich startet keine TSE-Transaktion.",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            Foreground = AppTheme.TextMuted
        };

        BarTestBonPlan PrepareBarTest()
        {
            var vat = barTestVat.SelectedIndex == 1 ? 7m : 19m;
            var plan = BarTestBonPreparation.Create(vat);
            barTestPlan = plan;
            barTestResult.Foreground = AppTheme.AccentTeal;
            barTestResult.Text =
                $"VORBEREITUNG OK · MwSt {vat:0} % · 1,00 EUR · BAR\n" +
                "Journal erwartet: CASH_READY → COMMITTED (BAR hat kein PREPARED/SENT)\n" +
                "TSE erwartet: OPEN → FINISHED\n" +
                $"ProcessType: {plan.ExpectedProcessType}\n" +
                $"ProcessData: {plan.ExpectedProcessData}\n" +
                "Hardwareprüfung später: Client-ID · TSE-Serial · Transaction Number · " +
                "Signature Counter · TSE-Zeiten · QR · FINISHED\n" +
                "JETZT: keine Buchung, kein Journal-Eintrag, keine TSE-Transaktion.";
            return plan;
        }

        barTestPrepare.Click += (_, _) => PrepareBarTest();

        barTestPrint.Click += async (_, _) =>
        {
            barTestPrint.IsEnabled = false;
            try
            {
                var plan = barTestPlan ?? PrepareBarTest();
                var values = await _settings.LoadAllAsync();
                if (!values.GetBool("device.receipt_printer.enabled", false))
                {
                    barTestResult.Foreground = AppTheme.WarningAmber;
                    barTestResult.Text += "\nDRUCK NICHT AUSGEFÜHRT: Bondrucker ist deaktiviert.";
                    return;
                }

                var printerName = values.GetText("device.receipt_printer.name", "").Trim();
                if (printerName.Length == 0)
                {
                    barTestResult.Foreground = AppTheme.WarningAmber;
                    barTestResult.Text += "\nDRUCK NICHT AUSGEFÜHRT: Kein Bondrucker ausgewählt.";
                    return;
                }

                var address = string.Join(" ", new[] { "company.street", "company.zip", "company.city" }
                    .Select(key => values.GetText(key, "").Trim())
                    .Where(value => value.Length > 0));

                var job = BarTestBonPreparation.BuildPreviewReceipt(
                    plan,
                    values.GetText("company.name", "TOR POS"),
                    address,
                    values.GetText("receipt.logo_path", ""));

                await _printer.PrintReceiptAsync(job, printerName).WaitAsync(TimeSpan.FromSeconds(5));
                barTestResult.Foreground = AppTheme.AccentTeal;
                barTestResult.Text +=
                    $"\n80-MM-TESTBON AN WINDOWS-SPOOLER ÜBERGEBEN: {printerName}\n" +
                    "Bon ist FISCAL TEST MODE, ohne TSE-QR und ohne Kassenschubladen-Impuls.";
            }
            catch (TimeoutException)
            {
                barTestResult.Foreground = new SolidColorBrush(Color.Parse("#FF8F9D"));
                barTestResult.Text += "\nDRUCK TIMEOUT: Windows-Druckpfad antwortete nicht innerhalb von 5 Sekunden.";
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("BAR TESTBON preparation print", ex);
                barTestResult.Foreground = new SolidColorBrush(Color.Parse("#FF8F9D"));
                barTestResult.Text += "\nDRUCK FEHLER: " + ex.Message;
            }
            finally
            {
                barTestPrint.IsEnabled = true;
            }
        };

        var barTestBonSection = Card(
            "BAR TESTBON · TSE-ABNAHMEVORBEREITUNG",
            "Kontrollierter Test: genau 1 Artikel, 1,00 EUR, BAR, wahlweise 19 % oder 7 %. " +
            "Kein Pfand, Angebot, Rabatt, Storno oder Karte. Vorbereitung und Testdruck schreiben weder Umsatz noch TSE-Daten.",
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        ItemSpacing = 8,
                        LineSpacing = 8,
                        Children = { barTestVat, barTestPrepare, barTestPrint }
                    },
                    barTestResult
                }
            });

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 18,
                Children =
                {
                    header,
                    buttons,
                    _status,
                    deviceSection,
                    barTestBonSection,
                    performanceSection,
                    recentSection,
                    errorLookupSection,
                    cardRefundSection,
                    orderPrintSection
                }
            }
        };

        Opened += async (_, _) =>
        {
            UiLanguage.Apply(this);
            RefreshPerformance();
            await RunAllAsync();
        };
    }

    private static Border Card(string title, string subtitle, Control content) =>
        new()
        {
            Background = new SolidColorBrush(Color.Parse("#111F30")),
            BorderBrush = new SolidColorBrush(Color.Parse("#29445D")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 19,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#E9F4FF"))
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = AppTheme.TextMuted
                    },
                    content
                }
            }
        };

    private async Task RunAllAsync()
    {
        if (!_runButton.IsEnabled)
            return;

        _runButton.IsEnabled = false;
        _status.Text = "Geräteprüfung läuft …";
        _deviceResults.Children.Clear();
        _deviceResults.Children.Add(StatusRow(
            "PRÜFUNG",
            "LÄUFT",
            "Externe Geräte werden parallel und mit Zeitlimit geprüft.",
            0,
            DiagnosticLevel.Neutral));

        var sw = Stopwatch.StartNew();
        try
        {
            var settings = await _settings.LoadAllAsync();

            var tasks = new Task<DiagnosticItem>[]
            {
                ProbePrinterAsync(settings),
                ProbeTerminalAsync(settings),
                ProbeCheckoutJournalAsync(),
                ProbeTseAsync(settings),
                ProbeSchemaAsync(),
                ProbeDatabaseHealthAsync(),
                ProbeAuthenticationKdfAsync(),
                ProbeAuditIntegrityAsync(),
                ProbePromotionsAsync(),
                ProbeStorageAsync(),
                ProbeBackupAsync(settings)
            };

            var results = await Task.WhenAll(tasks);

            _deviceResults.Children.Clear();
            foreach (var result in results)
            {
                _deviceResults.Children.Add(StatusRow(
                    result.Name,
                    result.State,
                    result.Message,
                    result.Milliseconds,
                    result.Level));
            }

            _status.Text = $"Geräteprüfung abgeschlossen · {sw.Elapsed.TotalMilliseconds:0} ms gesamt.";
            RefreshPerformance();
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("R67 diagnostics failed.", ex);
            _status.Text = "Diagnose fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            _runButton.IsEnabled = true;
        }
    }

    private async Task<DiagnosticItem> ProbePrinterAsync(
        IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.GetBool("device.receipt_printer.enabled", false))
            return DiagnosticItem.Disabled("BONDRUCKER", "In den Einstellungen deaktiviert.");

        var printerName = settings.GetText("device.receipt_printer.name", "").Trim();
        if (printerName.Length == 0)
            return DiagnosticItem.Warning("BONDRUCKER", "NICHT KONFIGURIERT", "Kein Windows-Drucker ausgewählt.");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CheckoutIo.WaitBoundedAsync(
                _printer.ProbeAsync(printerName, CancellationToken.None),
                TimeSpan.FromSeconds(2));
            sw.Stop();
            _performance.RecordElapsed("device.printer.probe", sw.Elapsed.TotalMilliseconds);

            return result.Success
                ? DiagnosticItem.Ok("BONDRUCKER", result.PrinterName.Length > 0 ? result.PrinterName : printerName, result.Message, sw.Elapsed.TotalMilliseconds)
                : DiagnosticItem.Warning("BONDRUCKER", "NICHT BEREIT", result.Message, sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed("device.printer.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("BONDRUCKER", "TIMEOUT", "Windows antwortete nicht innerhalb von 2 Sekunden.", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed("device.printer.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("BONDRUCKER", "FEHLER", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeTerminalAsync(
        IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.GetBool("payment.terminal.enabled", false))
            return DiagnosticItem.Disabled("KARTENTERMINAL", "In den Einstellungen deaktiviert.");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CheckoutIo.WaitBoundedAsync(
                _terminal.ProbeAsync(CancellationToken.None),
                TimeSpan.FromSeconds(3));
            sw.Stop();
            _performance.RecordElapsed("device.terminal.probe", sw.Elapsed.TotalMilliseconds);

            return result.Success
                ? DiagnosticItem.Ok("KARTENTERMINAL", result.State, $"{result.Message} · {result.Endpoint}", sw.Elapsed.TotalMilliseconds)
                : DiagnosticItem.Warning("KARTENTERMINAL", result.State, result.Message, sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed("device.terminal.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("KARTENTERMINAL", "TIMEOUT", "Terminal antwortete nicht innerhalb von 3 Sekunden.", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed("device.terminal.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("KARTENTERMINAL", "FEHLER", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeCheckoutJournalAsync()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var open =
                await CheckoutIo.WaitBoundedAsync(
                    _checkoutJournal.GetOpenAsync(),
                    TimeSpan.FromSeconds(2));

            sw.Stop();

            _performance.RecordElapsed(
                "checkout.journal.status",
                sw.Elapsed.TotalMilliseconds);

            if (open.Count == 0)
            {
                return DiagnosticItem.Ok(
                    "ZAHLUNGSJOURNAL",
                    "FREI",
                    "Keine offene oder ungeklärte Zahlung.",
                    sw.Elapsed.TotalMilliseconds);
            }

            var operation =
                open[0];

            var outcome =
                PaymentOutcomeCodec.ToStorage(
                    operation.TerminalOutcome);

            var resolution =
                PaymentOutcomeCodec.ToStorage(
                    operation.Resolution);

            var message =
                $"Status={operation.State}; " +
                $"Terminal={outcome}; " +
                $"gesendet={(operation.TerminalRequestSubmitted ? "JA" : "NEIN")}; " +
                $"Auflösung={resolution}; " +
                $"Vorgang={operation.Snapshot.OperationId}";

            return operation.State == "UNKNOWN" ||
                   operation.State == "SENT"
                ? DiagnosticItem.Error(
                    "ZAHLUNGSJOURNAL",
                    "GESPERRT",
                    message + " · Nicht erneut kassieren; zuerst ZAHLUNG PRÜFEN.",
                    sw.Elapsed.TotalMilliseconds)
                : DiagnosticItem.Warning(
                    "ZAHLUNGSJOURNAL",
                    operation.State,
                    message,
                    sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();

            _performance.RecordElapsed(
                "checkout.journal.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "ZAHLUNGSJOURNAL",
                "TIMEOUT",
                "Zahlungsjournal konnte innerhalb von 2 Sekunden nicht gelesen werden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();

            _performance.RecordElapsed(
                "checkout.journal.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "ZAHLUNGSJOURNAL",
                "FEHLER",
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeTseAsync(
        IReadOnlyDictionary<string, string> settings)
    {
        var configuredState = settings.GetText("tse.status", "NICHT_EINGERICHTET");
        if (!_tse.SdkAvailable && !string.Equals(configuredState, "AKTIV", StringComparison.OrdinalIgnoreCase))
            return DiagnosticItem.Disabled("TSE", "Noch nicht eingerichtet / Swissbit SDK nicht aktiv.");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CheckoutIo.WaitBoundedAsync(
                _tse.ProbeAsync(CancellationToken.None),
                TimeSpan.FromSeconds(3));
            sw.Stop();
            _performance.RecordElapsed("device.tse.probe", sw.Elapsed.TotalMilliseconds);

            return result.State switch
            {
                TseConnectionState.Ready =>
                    DiagnosticItem.Ok(
                        "TSE",
                        "BEREIT",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds),

                TseConnectionState.Connected =>
                    DiagnosticItem.Warning(
                        "TSE",
                        "VERBUNDEN · NOCH NICHT BEREIT",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds),

                TseConnectionState.Error =>
                    DiagnosticItem.Error(
                        "TSE",
                        "FEHLER",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds),

                TseConnectionState.SdkMissing =>
                    DiagnosticItem.Warning(
                        "TSE",
                        "SDK FEHLT",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds),

                TseConnectionState.NotFound =>
                    DiagnosticItem.Warning(
                        "TSE",
                        "NICHT GEFUNDEN",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds),

                _ =>
                    DiagnosticItem.Warning(
                        "TSE",
                        "NICHT EINGERICHTET",
                        result.Message,
                        sw.Elapsed.TotalMilliseconds)
            };
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed("device.tse.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("TSE", "TIMEOUT", "TSE antwortete nicht innerhalb von 3 Sekunden.", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed("device.tse.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("TSE", "FEHLER", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }



    private async Task<DiagnosticItem> ProbeDatabaseHealthAsync()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var status =
                await CheckoutIo.WaitBoundedAsync(
                    _databaseHealth.GetSnapshotAsync(
                        CancellationToken.None),
                    TimeSpan.FromSeconds(4));

            sw.Stop();

            _performance.RecordElapsed(
                "database.health",
                sw.Elapsed.TotalMilliseconds);

            var dbMb =
                status.DatabaseFileBytes /
                1024d /
                1024d;

            var walMb =
                status.WalFileBytes /
                1024d /
                1024d;

            var message =
                $"DB {dbMb:0.0} MB · WAL {walMb:0.0} MB · " +
                $"Pages {status.PageCount:N0} × {status.PageSizeBytes:N0} B · " +
                $"frei {status.FreeListPages:N0} ({status.FreeListPercent:0.0} %) · " +
                $"sync {status.SynchronousName} · busy {status.BusyTimeoutMs} ms · " +
                $"Auto-Checkpoint {status.WalAutoCheckpointPages:N0} Pages · " +
                $"PASSIVE busy/log/ckpt " +
                $"{status.CheckpointBusy}/{status.WalLogFrames}/{status.WalCheckpointedFrames} · " +
                $"quick_check {status.QuickCheck}";

            if (!status.IntegrityHealthy)
            {
                return DiagnosticItem.Error(
                    "SQLITE / WAL",
                    "INTEGRITÄTSFEHLER",
                    message,
                    sw.Elapsed.TotalMilliseconds);
            }

            if (!string.Equals(
                    status.JournalMode,
                    "wal",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DiagnosticItem.Error(
                    "SQLITE / WAL",
                    "WAL NICHT AKTIV",
                    message,
                    sw.Elapsed.TotalMilliseconds);
            }

            if (status.Synchronous != 2 ||
                !status.ForeignKeysEnabled)
            {
                return DiagnosticItem.Error(
                    "SQLITE / WAL",
                    "PRAGMA ABWEICHUNG",
                    message +
                    $" · foreign_keys={(status.ForeignKeysEnabled ? "ON" : "OFF")}",
                    sw.Elapsed.TotalMilliseconds);
            }

            if (status.BusyTimeoutMs < 3000)
            {
                return DiagnosticItem.Warning(
                    "SQLITE / WAL",
                    "BUSY-TIMEOUT PRÜFEN",
                    message,
                    sw.Elapsed.TotalMilliseconds);
            }

            if (status.CheckpointBusy != 0)
            {
                return DiagnosticItem.Warning(
                    "SQLITE / WAL",
                    "CHECKPOINT BESCHÄFTIGT",
                    message +
                    " · Aktive Reader/Writer verhinderten einen vollständigen PASSIVE-Lauf.",
                    sw.Elapsed.TotalMilliseconds);
            }

            // Size is diagnostic information, not a correctness failure.
            // A large WAL is deliberately not auto-truncated by Diagnose.
            var sizeState =
                status.WalFileBytes >= 256L * 1024 * 1024
                    ? "WAL GROSS"
                    : "OK";

            return sizeState == "OK"
                ? DiagnosticItem.Ok(
                    "SQLITE / WAL",
                    "OK",
                    message,
                    sw.Elapsed.TotalMilliseconds)
                : DiagnosticItem.Warning(
                    "SQLITE / WAL",
                    sizeState,
                    message +
                    " · Diagnose führt bewusst keinen TRUNCATE-Checkpoint aus.",
                    sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();

            _performance.RecordElapsed(
                "database.health",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "SQLITE / WAL",
                "TIMEOUT",
                "SQLite/WAL-Prüfung dauerte länger als 4 Sekunden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();

            _performance.RecordElapsed(
                "database.health",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "SQLITE / WAL",
                "FEHLER",
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeAuthenticationKdfAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var status = await CheckoutIo.WaitBoundedAsync(
                _authentication.GetKdfStatusAsync(CancellationToken.None),
                TimeSpan.FromSeconds(2));
            sw.Stop();
            _performance.RecordElapsed("auth.kdf.status", sw.Elapsed.TotalMilliseconds);

            if (status.UnsupportedSecretCount > 0)
                return DiagnosticItem.Error(
                    "AUTH-KDF",
                    "KONFIGURATION FEHLER",
                    $"{status.UnsupportedSecretCount} Passwort/PIN-Einträge haben unbekannte oder ungültige KDF-Metadaten. Anmeldung ist dafür fail-closed.",
                    sw.Elapsed.TotalMilliseconds);

            if (status.LegacySecretCount > 0)
                return DiagnosticItem.Warning(
                    "AUTH-KDF",
                    "UPGRADE BEI LOGIN",
                    $"{status.Algorithm} · Ziel {status.CurrentIterations:N0} Iterationen · {status.LegacySecretCount} ältere Hashes werden nach dem nächsten erfolgreichen Login automatisch angehoben.",
                    sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Ok(
                "AUTH-KDF",
                "AKTUELL",
                $"{status.Algorithm} · {status.CurrentIterations:N0} Iterationen · {status.Users} Benutzer · kein Legacy-Hash offen.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed("auth.kdf.status", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error(
                "AUTH-KDF",
                "TIMEOUT",
                "KDF-Status konnte innerhalb von 2 Sekunden nicht gelesen werden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed("auth.kdf.status", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("AUTH-KDF", "FEHLER", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeAuditIntegrityAsync()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var status = await CheckoutIo.WaitBoundedAsync(
                _controlledActions.VerifyIntegrityAsync(
                    CancellationToken.None),
                TimeSpan.FromSeconds(2));

            sw.Stop();
            _performance.RecordElapsed(
                "audit.integrity.verify",
                sw.Elapsed.TotalMilliseconds);

            if (!status.Valid)
            {
                return DiagnosticItem.Error(
                    "AUDIT-INTEGRITÄT",
                    "FEHLER",
                    status.Message,
                    sw.Elapsed.TotalMilliseconds);
            }

            return DiagnosticItem.Ok(
                "AUDIT-INTEGRITÄT",
                "OK",
                status.EntryCount == 0
                    ? "Hash-Kette bereit · noch keine kontrollierten Storno/Rabatt/Abbruch-Aktionen."
                    : $"{status.EntryCount} Einträge unverändert · Hash-Kette gültig.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "audit.integrity.verify",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "AUDIT-INTEGRITÄT",
                "TIMEOUT",
                "Audit-Integrität konnte innerhalb von 2 Sekunden nicht geprüft werden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "audit.integrity.verify",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "AUDIT-INTEGRITÄT",
                "FEHLER",
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbePromotionsAsync()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var campaigns = await CheckoutIo.WaitBoundedAsync(
                _promotions.GetAllAsync(CancellationToken.None),
                TimeSpan.FromSeconds(2));

            sw.Stop();
            _performance.RecordElapsed(
                "promotion.status",
                sw.Elapsed.TotalMilliseconds);

            var today = DateOnly.FromDateTime(DateTime.Now);
            var active = campaigns.Count(x => x.StatusFor(today) == "AKTIV");
            var planned = campaigns.Count(x => x.StatusFor(today) == "GEPLANT");
            var expired = campaigns.Count(x => x.StatusFor(today) == "ABGELAUFEN");

            return DiagnosticItem.Ok(
                "ANGEBOTE",
                active > 0 ? $"{active} AKTIV" : "KEIN AKTIVES",
                $"Aktiv {active} · geplant {planned} · abgelaufen {expired} · gesamt {campaigns.Count}",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "promotion.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "ANGEBOTE",
                "TIMEOUT",
                "Angebotsstatus konnte innerhalb von 2 Sekunden nicht gelesen werden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "promotion.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "ANGEBOTE",
                "FEHLER",
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeSchemaAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var status = await CheckoutIo.WaitBoundedAsync(
                _schemaMigrations.GetStatusAsync(CancellationToken.None),
                TimeSpan.FromSeconds(2));
            sw.Stop();
            _performance.RecordElapsed(
                "database.schema.status",
                sw.Elapsed.TotalMilliseconds);

            if (!status.MetadataPresent)
            {
                return DiagnosticItem.Error(
                    "DATENBANK-SCHEMA",
                    "NICHT VERSIONIERT",
                    $"Schema-Metadaten fehlen. Erwartet: Version {status.TargetVersion}.",
                    sw.Elapsed.TotalMilliseconds);
            }

            if (!status.IsCurrent)
            {
                return DiagnosticItem.Error(
                    "DATENBANK-SCHEMA",
                    "VERSION ABWEICHEND",
                    $"Ist {status.CurrentVersion} · Soll {status.TargetVersion} · Historie {status.HistoryCount}",
                    sw.Elapsed.TotalMilliseconds);
            }

            var updated = DateTimeOffset.TryParse(status.UpdatedAt, out var parsed)
                ? parsed.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")
                : status.UpdatedAt;

            return DiagnosticItem.Ok(
                "DATENBANK-SCHEMA",
                $"V{status.CurrentVersion} AKTUELL",
                $"Versionierte Migrationen aktiv · Historie {status.HistoryCount} · zuletzt {updated}",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "database.schema.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "DATENBANK-SCHEMA",
                "TIMEOUT",
                "Schema-Status konnte innerhalb von 2 Sekunden nicht gelesen werden.",
                sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed(
                "database.schema.status",
                sw.Elapsed.TotalMilliseconds);

            return DiagnosticItem.Error(
                "DATENBANK-SCHEMA",
                "FEHLER",
                ex.Message,
                sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<DiagnosticItem> ProbeStorageAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CheckoutIo.WaitBoundedAsync(
                Task.Run(() =>
                {
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    var testPath = Path.Combine(
                        AppPaths.DataDirectory,
                        $".tor-write-test-{Environment.ProcessId}.tmp");
                    File.WriteAllText(testPath, "TOR");
                    File.Delete(testPath);

                    var root = Path.GetPathRoot(AppPaths.DataDirectory);
                    var freeGb = 0d;
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        var drive = new DriveInfo(root);
                        if (drive.IsReady)
                            freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                    }

                    var dbExists = File.Exists(AppPaths.DatabasePath);
                    var dbSizeMb = dbExists
                        ? new FileInfo(AppPaths.DatabasePath).Length / 1024d / 1024d
                        : 0d;

                    return (dbExists, dbSizeMb, freeGb);
                }),
                TimeSpan.FromSeconds(2));
            sw.Stop();
            _performance.RecordElapsed("storage.write.probe", sw.Elapsed.TotalMilliseconds);

            var state = result.dbExists ? "BEREIT" : "DATENBANK FEHLT";
            var message = result.dbExists
                ? $"Datenordner beschreibbar · DB {result.dbSizeMb:0.0} MB · frei {result.freeGb:0.0} GB"
                : $"Datenordner beschreibbar, aber Datenbankdatei wurde nicht gefunden · frei {result.freeGb:0.0} GB";

            return result.dbExists
                ? DiagnosticItem.Ok("DATENSPEICHER", state, message, sw.Elapsed.TotalMilliseconds)
                : DiagnosticItem.Warning("DATENSPEICHER", state, message, sw.Elapsed.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            _performance.RecordElapsed("storage.write.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("DATENSPEICHER", "TIMEOUT", "Datenträger antwortete nicht innerhalb von 2 Sekunden.", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _performance.RecordElapsed("storage.write.probe", sw.Elapsed.TotalMilliseconds);
            return DiagnosticItem.Error("DATENSPEICHER", "FEHLER", ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private Task<DiagnosticItem> ProbeBackupAsync(
        IReadOnlyDictionary<string, string> settings)
    {
        var lastSuccess = settings.GetText("backup.daily.last_success", "").Trim();
        var lastError = settings.GetText("backup.daily.last_error", "").Trim();
        var lastPath = settings.GetText("backup.daily.last_path", "").Trim();

        if (lastSuccess.Length == 0)
        {
            return Task.FromResult(DiagnosticItem.Warning(
                "DATENSICHERUNG",
                "NOCH KEINE",
                lastError.Length > 0 ? lastError : "Noch keine erfolgreiche tägliche Sicherung protokolliert."));
        }

        var text = DateTimeOffset.TryParse(lastSuccess, out var timestamp)
            ? timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")
            : lastSuccess;

        var message = $"Letzte erfolgreiche Sicherung: {text}";
        if (lastPath.Length > 0)
            message += $" · {lastPath}";
        if (lastError.Length > 0)
            message += $" · letzter Fehler: {lastError}";

        return Task.FromResult(lastError.Length == 0
            ? DiagnosticItem.Ok("DATENSICHERUNG", "OK", message)
            : DiagnosticItem.Warning("DATENSICHERUNG", "PRÜFEN", message));
    }

    private async Task RefreshCardRefundLocksAsync()
    {
        _cardRefundResults.Children.Clear();
        IReadOnlyList<CardRefundAttempt> attempts;
        try
        {
            attempts = await _cardRefundLocks.GetUnresolvedAsync();
        }
        catch (Exception ex)
        {
            _cardRefundResults.Children.Add(new TextBlock
            {
                Text = "Konnte nicht geladen werden: " + ex.Message,
                Foreground = new SolidColorBrush(Color.Parse("#E38686"))
            });
            return;
        }

        if (attempts.Count == 0)
        {
            _cardRefundResults.Children.Add(new TextBlock
            {
                Text = "Keine blockierten Kartenerstattungen.",
                Foreground = AppTheme.TextMuted
            });
            return;
        }

        foreach (var attempt in attempts)
        {
            var actorBox = new TextBox { PlaceholderText = "Bearbeiter", MinWidth = 160, MinHeight = 38 };
            var noteBox = new TextBox { PlaceholderText = "Ergebnis der Prüfung (z. B. 'Terminal/Bank bestätigt: keine Belastung')", MinWidth = 320, MinHeight = 38 };
            var resolveButton = new Button { Content = "ALS GEKLÄRT MARKIEREN", MinHeight = 38 };
            var rowStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextMuted };

            resolveButton.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(actorBox.Text))
                {
                    rowStatus.Text = "Bearbeiter angeben.";
                    return;
                }
                resolveButton.IsEnabled = false;
                try
                {
                    await _cardRefundLocks.ResolveAsync(attempt.Id, actorBox.Text!.Trim(), noteBox.Text ?? "");
                    await RefreshCardRefundLocksAsync();
                }
                catch (Exception ex)
                {
                    rowStatus.Text = "Fehler: " + ex.Message;
                    resolveButton.IsEnabled = true;
                }
            };

            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1B2E42")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Bon {attempt.OriginalReceiptNumber:000000} · {attempt.Kind} · {attempt.AmountCents / 100m:0.00} EUR · seit {attempt.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm}",
                            FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White
                        },
                        new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { actorBox, noteBox, resolveButton } },
                        rowStatus
                    }
                }
            };
            _cardRefundResults.Children.Add(row);
        }
    }

    /// <summary>
    /// R119 follow-up: kitchen tickets the print queue gave up on. Before the
    /// queue could give up at all, one unreachable printer blocked every later
    /// ticket forever; now the queue keeps moving and the abandoned tickets
    /// need somewhere to be seen and re-queued.
    /// </summary>
    private async Task RefreshOrderPrintFailuresAsync()
    {
        _orderPrintResults.Children.Clear();
        IReadOnlyList<(string Id, long OrderId, string Action, int Attempts, string LastError, string CreatedAt)> failures;
        try
        {
            failures = await _orderPrints.FailedJobsAsync();
        }
        catch (Exception ex)
        {
            _orderPrintResults.Children.Add(new TextBlock
            {
                Text = "Konnte nicht geladen werden: " + ex.Message,
                Foreground = new SolidColorBrush(Color.Parse("#E38686"))
            });
            return;
        }

        if (failures.Count == 0)
        {
            _orderPrintResults.Children.Add(new TextBlock
            {
                Text = "Keine blockierten Küchenbons.",
                Foreground = AppTheme.TextMuted
            });
            return;
        }

        foreach (var job in failures)
        {
            var rowStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.TextMuted };
            var retryButton = new Button { Content = "ERNEUT DRUCKEN", MinHeight = 38 };

            retryButton.Click += async (_, _) =>
            {
                retryButton.IsEnabled = false;
                try
                {
                    await _orderPrints.RetryFailedAsync(job.Id);
                    await RefreshOrderPrintFailuresAsync();
                }
                catch (Exception ex)
                {
                    rowStatus.Text = "Fehler: " + ex.Message;
                    retryButton.IsEnabled = true;
                }
            };

            var created = DateTimeOffset.TryParse(job.CreatedAt, out var at)
                ? at.LocalDateTime.ToString("dd.MM.yyyy HH:mm")
                : job.CreatedAt;

            _orderPrintResults.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1B2E42")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Bestellung {job.OrderId} · {job.Action} · {job.Attempts} Versuche · seit {created}",
                            FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White
                        },
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(job.LastError) ? "Kein Fehlertext gespeichert." : job.LastError,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = AppTheme.WarningAmber
                        },
                        new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { retryButton } },
                        rowStatus
                    }
                }
            });
        }
    }

    private void RefreshPerformance()
    {
        _performanceResults.Children.Clear();
        var snapshots = _performance.SnapshotAll();

        if (snapshots.Count == 0)
        {
            _performanceResults.Children.Add(new TextBlock
            {
                Text = "Noch keine Messwerte vorhanden.",
                Foreground = AppTheme.TextMuted
            });
        }
        else
        {
            foreach (var item in snapshots.OrderByDescending(x => x.Value.MaxMs))
            {
                var slow = item.Value.SlowCount > 0;
                _performanceResults.Children.Add(new TextBlock
                {
                    Text =
                        $"{item.Key,-32}  Letzt {item.Value.LastMs,7:0.0} ms · " +
                        $"Ø {item.Value.AverageMs,7:0.0} ms · Max {item.Value.MaxMs,7:0.0} ms · " +
                        $"{item.Value.Count}x · langsam {item.Value.SlowCount}x",
                    FontFamily = FontFamily.Default,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = slow ? AppTheme.WarningAmber : new SolidColorBrush(Color.Parse("#D7E6F4"))
                });
            }
        }

        _recentResults.Children.Clear();
        var recent = _performance.Recent(30);
        if (recent.Count == 0)
        {
            _recentResults.Children.Add(new TextBlock
            {
                Text = "Noch keine Messungen.",
                Foreground = AppTheme.TextMuted
            });
        }
        else
        {
            foreach (var sample in recent)
            {
                _recentResults.Children.Add(new TextBlock
                {
                    Text = $"{sample.Timestamp:HH:mm:ss} · {sample.Name} · {sample.Milliseconds:0.0} ms" +
                           (sample.IsSlow ? " · LANGSAM" : ""),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = sample.IsSlow ? AppTheme.WarningAmber : AppTheme.TextMuted
                });
            }
        }
    }

    private static Border StatusRow(
        string name,
        string state,
        string message,
        double milliseconds,
        DiagnosticLevel level)
    {
        var color = level switch
        {
            DiagnosticLevel.Ok => AppTheme.AccentTeal,
            DiagnosticLevel.Warning => AppTheme.WarningAmber,
            DiagnosticLevel.Error => new SolidColorBrush(Color.Parse("#FF8F9D")),
            _ => AppTheme.AccentBlue
        };

        var timing = milliseconds > 0 ? $" · {milliseconds:0} ms" : "";

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0D1A28")),
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("170,170,*"),
                ColumnSpacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = name,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text = state + timing,
                        FontWeight = FontWeight.Bold,
                        Foreground = color,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 2,
                        Text = message,
                        Foreground = AppTheme.TextMuted,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
    }

    private enum DiagnosticLevel
    {
        Neutral,
        Ok,
        Warning,
        Error
    }

    private sealed record DiagnosticItem(
        string Name,
        string State,
        string Message,
        double Milliseconds,
        DiagnosticLevel Level)
    {
        public static DiagnosticItem Ok(string name, string state, string message, double ms = 0) =>
            new(name, state, message, ms, DiagnosticLevel.Ok);

        public static DiagnosticItem Warning(string name, string state, string message, double ms = 0) =>
            new(name, state, message, ms, DiagnosticLevel.Warning);

        public static DiagnosticItem Error(string name, string state, string message, double ms = 0) =>
            new(name, state, message, ms, DiagnosticLevel.Error);

        public static DiagnosticItem Disabled(string name, string message) =>
            new(name, "AUS", message, 0, DiagnosticLevel.Neutral);
    }
}
