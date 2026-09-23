using System.Diagnostics;
using TorPos.Infrastructure;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TorPos.Core;
namespace TorPos.App;

public partial class MainWindow
{
    private async Task ShowMenuInfoAsync(string title, string message)
    {
        StatusLine = message;
        var window = new Window { Title = title, Width = 600, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "SCHLIESSEN", MinHeight = 48, MinWidth = 150 };
        close.Click += (_, _) => window.Close();
        window.Content = new ScrollViewer { Content = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 20,
            Children = { new TextBlock { Text = title, FontSize = 22 },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close } } };
        UiLanguage.Apply(window);
        await window.ShowDialog(this);
    }

    // Shown once per program start when the TSE is not ready. A missing TSE is
    // not a crash and must not stop the till - § 146a expects the outage to be
    // documented and the shop to keep working, which TseFailSafeService already
    // does. What was missing is that anyone notices: the status line under the
    // scanner is overwritten by the next scan, and the header badge is easy to
    // work past for a whole day. So the operator is told once, plainly, with the
    // way to fix it if they are allowed to.
    private async Task ShowTseUnavailableAsync(string headline, string deviceMessage)
    {
        var window = new Window
        {
            Title = "TSE-Prüfung beim Start",
            Width = 700,
            Height = 430,
            MinWidth = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var openSettings = false;
        var settings = new Button
        {
            Content = "TSE-EINSTELLUNGEN ÖFFNEN",
            MinHeight = 48,
            MinWidth = 220,
            IsVisible = _currentUser.IsAdmin
        };
        var close = new Button { Content = "WEITER OHNE TSE", MinHeight = 48, MinWidth = 180 };
        settings.Click += (_, _) => { openSettings = true; window.Close(); };
        close.Click += (_, _) => window.Close();

        // Scrolls rather than clips. This window is built inline, so the
        // headless layout gate - which constructs window classes - never
        // measures it, and the Turkish and English sentences are longer than
        // the German ones they are keyed by.
        window.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = headline,
                        FontSize = 23,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Foreground = AppTheme.WarningAmber
                    },
                    new TextBlock
                    {
                        Text = deviceMessage,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 15,
                        Opacity = 0.85
                    },
                    // One literal, not a concatenation: the translation table is
                    // keyed by the exact German sentence, and a key that only
                    // exists once the compiler has glued three pieces together
                    // cannot be found by grep or by a coverage check.
                    new TextBlock
                    {
                        Text = "Bis eine betriebsbereite TSE erkannt wird, wird kein Vorgang signiert. Die Kasse bleibt bedienbar und der Ausfall wird dokumentiert; die Vorgänge sind dann aber nicht fiskal abgesichert.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 15
                    },
                    new TextBlock
                    {
                        Text = _currentUser.IsAdmin
                            ? "Prüfen: steckt die TSE im USB-Anschluss, wird sie im Explorer als Laufwerk angezeigt, ist der Techniker-Bereich eingerichtet?"
                            : "Bitte die Betreiberin oder den Betreiber informieren. Der Verkauf kann weiterlaufen.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontSize = 15,
                        Foreground = AppTheme.AccentBlue
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 10,
                        Children = { settings, close }
                    }
                }
            }
        };

        UiLanguage.Apply(window);
        await window.ShowDialog(this);
        if (openSettings)
            await OpenSettingsPageAsync("Erweitert / Techniker 🔒");
    }

    private async Task ShowPrinterIssueAsync(string title, string message, bool uncertainQueue)
    {
        StatusLine = message;
        var window = new Window
        {
            Title = title,
            Width = 690,
            Height = 370,
            MinWidth = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        string action = "";
        var settings = new Button { Content = "DRUCKEREINSTELLUNGEN", MinHeight = 48, MinWidth = 190 };
        var queue = new Button { Content = "DRUCKWARTESCHLANGE PRÜFEN", MinHeight = 48, MinWidth = 220, IsVisible = uncertainQueue };
        var close = new Button { Content = "SCHLIESSEN", MinHeight = 48, MinWidth = 140 };
        settings.Click += (_, _) => { action = "settings"; window.Close(); };
        queue.Click += (_, _) => { action = "queue"; window.Close(); };
        close.Click += (_, _) => window.Close();

        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 23, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 15 },
                new TextBlock
                {
                    Text = uncertainQueue
                        ? "Wichtig: Nach einem Timeout nicht blind erneut drucken. Der alte Auftrag kann noch in Windows liegen."
                        : "Prüfen: Drucker eingeschaltet, USB/LAN verbunden, Papier eingelegt und der richtige Windows-Drucker ausgewählt.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = uncertainQueue ? AppTheme.WarningAmber : AppTheme.AccentBlue
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 10,
                    Children = { settings, queue, close }
                }
            }
        };

        UiLanguage.Apply(window);
        await window.ShowDialog(this);
        if (action == "settings")
            await OpenSettingsPageAsync("Geräte");
        else if (action == "queue")
            OnPrinterReviewClick(null, new RoutedEventArgs());
    }


    private bool _checkoutWithoutPrinterAccepted;

    private async Task<bool> ConfirmCheckoutWithoutPrinterAsync(string title, string message)
    {
        StatusLine = message;
        var window = new Window
        {
            Title = title,
            Width = 720,
            Height = 390,
            MinWidth = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var result = false;
        var action = "cancel";
        var continueButton = new Button { Content = "OHNE DRUCKER FORTFAHREN", MinHeight = 50, MinWidth = 220 };
        var settingsButton = new Button { Content = "DRUCKEREINSTELLUNGEN", MinHeight = 50, MinWidth = 190 };
        var cancelButton = new Button { Content = "ABBRECHEN", MinHeight = 50, MinWidth = 140 };
        continueButton.Click += (_,_) => { action = "continue"; result = true; window.Close(); };
        settingsButton.Click += (_,_) => { action = "settings"; result = false; window.Close(); };
        cancelButton.Click += (_,_) => { action = "cancel"; result = false; window.Close(); };
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 24, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 16 },
                new TextBlock
                {
                    Text = "BAR/KARTE wird standardmäßig abgebrochen. Nur wenn bewusst ohne Bondruck fortgefahren werden soll, OHNE DRUCKER FORTFAHREN wählen.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = AppTheme.WarningAmber
                },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10,
                    Children = { continueButton, settingsButton, cancelButton } }
            }
        };
        UiLanguage.Apply(window);
        await window.ShowDialog(this);
        if (action == "settings")
            await OpenSettingsPageAsync("Geräte");
        return result;
    }

    private async Task<bool> EnsureReceiptPrinterReadyForCheckoutAsync()
    {
        _checkoutWithoutPrinterAccepted = false;
        if (!_settingsCache.GetBool("receipt.auto_print", true)) return true;
        var printer = _settingsCache.GetText("device.receipt_printer.name", "").Trim();
        if (!_settingsCache.GetBool("device.receipt_printer.enabled", false) || printer.Length == 0)
        {
            var proceed = await ConfirmCheckoutWithoutPrinterAsync(
                "BONDRUCKER NICHT ERKANNT",
                "Es ist kein aktiver Bondrucker eingerichtet. Die Zahlung wurde noch nicht gestartet.");
            _checkoutWithoutPrinterAccepted = proceed;
            return proceed;
        }
        try
        {
            StatusLine = "BONDRUCKER WIRD GEPRÜFT · maximal 2 Sekunden";
            // R67.2: Only actual Windows/device I/O is measured here.
            // Warning-dialog reading time must never be reported as POS latency.
            var probeStarted = Stopwatch.GetTimestamp();
            PrinterProbeResult probe;
            try
            {
                var probeTask = _receiptPrinter.ProbeAsync(printer, CancellationToken.None);
                probe = await CheckoutIo.WaitBoundedAsync(
                    probeTask,
                    TimeSpan.FromSeconds(2));
            }
            finally
            {
                _perf.RecordElapsed(
                    "checkout.printer_probe",
                    Stopwatch.GetElapsedTime(probeStarted).TotalMilliseconds);
            }

            if (!probe.Success)
            {
                var proceed = await ConfirmCheckoutWithoutPrinterAsync(
                    "BONDRUCKER NICHT BEREIT",
                    probe.Message + "\n\nDie Zahlung wurde noch nicht gestartet.");
                _checkoutWithoutPrinterAccepted = proceed;
                return proceed;
            }

            StatusLine = "BONDRUCKER BEREIT";
            return true;
        }
        catch (TimeoutException)
        {
            var proceed = await ConfirmCheckoutWithoutPrinterAsync(
                "BONDRUCKER ANTWORTET NICHT",
                "Windows konnte den Druckerstatus innerhalb von 2 Sekunden nicht bestätigen. " +
                "Die Kasse bleibt bedienbar und die Zahlung wurde noch nicht gestartet.\n\n" +
                "USB/LAN, Strom und Windows-Druckwarteschlange prüfen.");
            _checkoutWithoutPrinterAccepted = proceed;
            return proceed;
        }
        catch (Exception ex)
        {
            var proceed = await ConfirmCheckoutWithoutPrinterAsync(
                "BONDRUCKER NICHT ERKANNT",
                "Der Druckerstatus konnte nicht bestätigt werden: " + ex.Message + "\n\nDie Zahlung wurde noch nicht gestartet.");
            _checkoutWithoutPrinterAccepted = proceed;
            return proceed;
        }
    }

    /// <param name="withoutPrinterAccepted">R145: the cashier's decision at checkout, taken over when printing follows a later receipt choice.</param>
    /// <param name="explicitRequest">R145: the customer asked for paper, so BON EIN/AUS does not suppress it.</param>
    private async Task PrintSimulationAsync(ReceiptPrintJob job, bool? withoutPrinterAccepted = null, bool explicitRequest = false)
    {
        if (withoutPrinterAccepted ?? _checkoutWithoutPrinterAccepted)
        {
            StatusLine = $"{StatusLine} · {UiLanguage.T("BONDRUCKER NICHT ERKANNT · ohne Druck fortgesetzt")}";
            return;
        }
        if (!explicitRequest && !_settingsCache.GetBool("receipt.auto_print", true))
        { StatusLine = $"{StatusLine} · {UiLanguage.T("BON AUS: kein Testdruck")}"; return; }
        var printer = _settingsCache.GetText("device.receipt_printer.name", "");
        if (!_settingsCache.GetBool("device.receipt_printer.enabled", false) || string.IsNullOrWhiteSpace(printer))
        {
            await ShowPrinterIssueAsync(
                "KEIN BONDRUCKER EINGERICHTET",
                "Der Testverkauf wurde nur simuliert; es wurde kein echter Verkauf gespeichert. Es ist kein Bondrucker aktiviert oder ausgewählt.",
                uncertainQueue: false);
            return;
        }

        try
        {
            // R65: auch der Simulationsdruck darf niemals synchron die Windows-
            // Druckerliste auf dem UI-Thread abfragen.
            var probe = await CheckoutIo.WaitBoundedAsync(
                _receiptPrinter.ProbeAsync(printer, CancellationToken.None),
                TimeSpan.FromSeconds(2));

            if (!probe.Success)
            {
                await ShowPrinterIssueAsync(
                    "BONDRUCKER NICHT BEREIT",
                    probe.Message + "\n\nDer Testverkauf wurde nicht gedruckt und es wurde kein echter Verkauf gespeichert.",
                    uncertainQueue: false);
                return;
            }

            await _receiptPrinter.PrintReceiptAsync(job, printer);
            StatusLine = "TESTBON an Windows übergeben · Papierausdruck prüfen · keine echte Buchung";
        }
        catch (TimeoutException)
        {
            await ShowPrinterIssueAsync(
                "BONDRUCKER ANTWORTET NICHT",
                "Windows konnte den Druckerstatus innerhalb von 2 Sekunden nicht bestätigen. " +
                "Der Testverkauf ist abgeschlossen, es wurde kein echter Verkauf gespeichert und die Kasse bleibt bedienbar.",
                uncertainQueue: false);
        }
        catch (Exception ex)
        {
            var id = ReportOperationalError("DRUCKER", "Testbon konnte nicht übergeben werden.", ex, printerRelated: true);
            var uncertain = ex is TimeoutException ||
                ex.Message.Contains("unklar", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Vorheriger Druckauftrag", StringComparison.OrdinalIgnoreCase);
            await ShowPrinterIssueAsync(
                uncertain ? "DRUCKSTATUS UNKLAR" : "BONDRUCKER NICHT BEREIT",
                "Simulation abgeschlossen; kein echter Verkauf gespeichert.\n\n" +
                (uncertain
                    ? "Der Drucker hat nicht rechtzeitig geantwortet. USB/LAN, Strom, Papier und die Windows-Druckwarteschlange prüfen."
                    : "Der Druckauftrag konnte nicht an den gewählten Drucker übergeben werden.") +
                $"\n\nFehler-ID: {id}",
                uncertain);
        }
    }

    private bool _closingPrepared;
    private bool _closingInProgress;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if(_paymentInProgress) { e.Cancel=true; StatusLine="Zahlung läuft · bitte Ergebnis abwarten"; }
        else if(!_closingPrepared)
        {
            e.Cancel=true;
            if(!_closingInProgress) { _closingInProgress=true; _=PrepareCloseAsync(); }
        }
        base.OnClosing(e);
    }
    private async Task PrepareCloseAsync()
    {
        try
        {
            PersistOpenCartRecovery();
            await _recoveryWrite.WaitAsync(TimeSpan.FromSeconds(8));
            if(_pendingCheckout is null && _activeParkedReceiptId is long id && _engine.Cart.Count>0)
                await _parkedReceipts.UpdateAsync(id,CheckoutSnapshot.CopyLines(_engine.Cart),_engine.DiscountCents).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch(Exception ex) { CrashLog.WriteException("Closing recovery/park save failed",ex); }
        finally { _closingPrepared=true; Close(); }
    }

    private async void OnPrinterReviewClick(object? sender, RoutedEventArgs e)
    {
        if(_paymentInProgress) return;
        if(_currentUser.IsTraining) { await ShowMenuInfoAsync("TRAINING", "Diese Prüfung ist im Trainingsmodus deaktiviert. Mit einem regulären Benutzer anmelden."); return; }
        try
        {
            if(_receiptPrinter is not TorPos.Infrastructure.StarMcPrint3PrinterService activePrinter) return;
            var jobs=await activePrinter.GetUncertainJobsAsync();
            var pendingOrders=await new OrderPrintOutbox(new SqliteDatabase(AppPaths.DatabasePath)).PendingAsync();
            if(jobs.Count==0) { await ShowMenuInfoAsync("DRUCKWARTESCHLANGE PRÜFEN", "Wartende Bestelldrucke: "+pendingOrders.Count+" (bis 25 angezeigt). Keine unklaren Druckaufträge im TOR-Druckjournal.\nDies bestätigt keinen Papierausdruck. Den tatsächlichen Druckstatus bei Bedarf zusätzlich in Windows prüfen."); return; }
            var dialog=new Window {Title="DRUCKWARTESCHLANGE PRÜFEN",Width=620,Height=500,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var password=new TextBox {PasswordChar='●',PlaceholderText="Admin-Passwort"};
            var proof=new TextBox {PlaceholderText="Prüfnachweis: Windows-Warteschlange / bereits gedruckte Belege",AcceptsReturn=true,Height=75};
            var status=new TextBlock {TextWrapping=Avalonia.Media.TextWrapping.Wrap};
            var confirm=new Button {Content="Geprüft · Sperre aufheben (kein automatischer Nachdruck)"};
            confirm.Click+=async (_,_)=>
            {
                confirm.IsEnabled=false;
                try
                {
                    var login=await _authentication.LoginWithPasswordAsync("admin",password.Text??"");
                    if(!login.Success || login.User?.IsAdmin!=true || login.User.MustChangePassword) throw new InvalidOperationException("Eingerichteter Admin-Zugang erforderlich.");
                    if(_receiptPrinter is not TorPos.Infrastructure.StarMcPrint3PrinterService printer) return;
                    if((proof.Text??"").Trim().Length<8) throw new InvalidOperationException("Prüfnachweis fehlt (mindestens 8 Zeichen).");
                    await printer.ResolveQueueAsync("admin="+login.User.Username+"; "+(proof.Text??""));
                    CrashLog.Write("PRINT-QUEUE-REVIEWED; admin="+login.User.Username+"; evidence="+proof.Text);
                    dialog.Close();
                }
                catch(Exception ex) {status.Text=ex.Message;confirm.IsEnabled=true;}
            };
            dialog.Content=new StackPanel {Margin=new Avalonia.Thickness(22),Spacing=12,Children={
                new TextBlock {Text="Zuerst Windows-Druckwarteschlange und Papierbelege prüfen. Noch laufende Aufträge können später drucken.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},
                new TextBox {IsReadOnly=true,AcceptsReturn=true,Height=160,Text=string.Join("\n",jobs.Select(j=>$"{j.State} · {(j.Kitchen is not null ? "Küche P"+j.Kitchen.ParkNumber : j.PickupSlip is not null ? "Abholschein P"+j.PickupSlip.ParkNumber : j.Report is not null ? "Bericht " + j.Report.Title : j.Error is not null ? "Fehler " + j.Error.ErrorId : "Bon " + j.Receipt?.ReceiptNumber)} · {j.Id}"))},password,proof,confirm,status}};
            UiLanguage.Apply(dialog);
            await dialog.ShowDialog(this);
        }
        catch(Exception ex) { ReportOperationalError("DRUCKER","Druckjournal prüfen.",ex,printerRelated:true); }
    }

    private async void OnCheckoutReviewClick(object? sender, RoutedEventArgs e)
    {
        if(_paymentInProgress) return;
        if(_currentUser.IsTraining) { await ShowMenuInfoAsync("TRAINING", "Diese Prüfung ist im Trainingsmodus deaktiviert. Mit einem regulären Benutzer anmelden."); return; }
        CheckoutOperation? operation=null;
        try
        {
            var open=await _checkoutJournal.GetOpenAsync();
            operation=open.FirstOrDefault();
            if(operation is null) { await ShowMenuInfoAsync("ZAHLUNG PRÜFEN", "Keine ungeklärte Zahlung im Journal. Es ist keine Freigabe oder erneute Zahlung erforderlich."); return; }
            var decision=await new CheckoutReviewWindow(operation,_authentication).ShowDialog<CheckoutReviewResult?>(this);
            if(decision is null) return;
            SetCheckoutBusy(true);
            var proof=$"admin={decision.Administrator}; evidence={decision.Evidence}";
            var reconciliation =
                await _checkoutApplication.ReconcileAsync(
                    operation,
                    decision.Paid,
                    decision.Administrator,
                    proof);

            if(decision.Paid)
            {
                // Preserve known-paid state even when fiscal release is still blocked.
                if(!reconciliation.ShouldCommit)
                {
                    StatusLine=
                        "ZAHLUNG MANUELL BESTÄTIGT · Buchung bleibt bis Fiskal-Freigabe gesperrt · nicht erneut kassieren";
                    return;
                }

                await CommitCheckoutAsync(
                    operation.Snapshot);
            }
            else
            {
                _pendingCheckout=null;
                _engine.IsReadOnly=false;

                var restaurantPayment =
                    await _restaurant.HasPreparedPaymentReservationAsync(
                        operation.Snapshot.OperationId);

                if (restaurantPayment)
                {
                    await _restaurant.CancelPaymentReservationAsync(
                        operation.Snapshot.OperationId);
                    _restaurantCheckoutDraft = null;
                    _operationId = Guid.NewGuid().ToString("N");
                    StatusLine =
                        "KEINE BELASTUNG MANUELL BESTÄTIGT · Restaurant-Tisch wieder offen";
                }
                else
                {
                    _engine.Restore(
                        operation.Snapshot.Lines,
                        operation.Snapshot.DiscountCents);

                    _activeParkedReceiptId=
                        operation.Snapshot.ParkedReceiptId;

                    _operationId=
                        Guid.NewGuid().ToString("N");

                    UpdateCart();

                    StatusLine=
                        "KEINE BELASTUNG MANUELL BESTÄTIGT · Bon wieder offen";
                }
            }
        }
        catch(Exception ex)
        {
            CrashLog.WriteException("MainWindow operation", ex);
            var id=ReportOperationalError("ZAHLUNGSPRÜFUNG","Prüfung nicht abgeschlossen. Zahlung gesperrt lassen.",ex);
            StatusLine=$"PRÜFUNG NICHT ABGESCHLOSSEN · NICHT ERNEUT KASSIEREN · Fehler-ID {id}";
        }
        finally
        {
            try
            {
                _pendingCheckout=(await _checkoutJournal.GetOpenAsync()).FirstOrDefault();
                if(operation is not null &&
                   await _checkoutJournal.FindSaleAsync(operation.Snapshot.OperationId) is not null)
                    ClearCompletedCart();
            }
            catch { _recoveryFault=true; }
            SetCheckoutBusy(false);
        }
    }
}
