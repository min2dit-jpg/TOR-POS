using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

/// <summary>R19: deliberately small first-run assistant. It never activates a TSE
/// and never performs a payment. Hardware buttons are probe/test-only.</summary>
public sealed class FirstRunSetupWindow : Window
{
    private readonly ISettingsRepository _settings;
    private readonly IReceiptPrinterService _printer;
    private readonly ITseProvider _tse;
    private readonly IPaymentTerminalService _terminal;
    private readonly string _edition;
    private readonly Grid _host = new();
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeight.Bold };
    private readonly TextBlock _stepText = new() { Foreground = Brushes.Gray };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 34 };
    private readonly Button _back = new() { Content = "ZURÜCK", MinWidth = 130, Height = 48 };
    private readonly Button _next = new() { Content = "WEITER", MinWidth = 150, Height = 48 };
    private int _step;
    private readonly TextBox _company = new();
    private readonly TextBox _owner = new();
    private readonly TextBox _street = new();
    private readonly TextBox _zip = new();
    private readonly TextBox _city = new();
    private readonly ComboBox _printerName = new();
    private readonly CheckBox _printerEnabled = new() { Content = "Bondrucker verwenden", IsChecked = true };
    private readonly CheckBox _terminalEnabled = new() { Content = "Automatische Kartenterminal-Anbindung verwenden" };
    private readonly ComboBox _terminalProfile = new() { MinHeight = 42, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _terminalProfileHint = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
    private readonly TextBox _terminalIp = new() { PlaceholderText = "z. B. 192.168.1.50" };
    private readonly TextBox _terminalPort = new() { Text = "20007" };
    private readonly TerminalProfileChoice[] _terminalProfiles;

    public bool Completed { get; private set; }

    public FirstRunSetupWindow(ISettingsRepository settings, IReceiptPrinterService printer,
        ITseProvider tse, IPaymentTerminalService terminal, string edition)
    {
        _settings = settings; _printer = printer; _tse = tse; _terminal = terminal; _edition = edition;
        _terminalProfiles = _terminal.Profiles.Select(x => new TerminalProfileChoice(x)).ToArray();
        _terminalProfile.ItemsSource = _terminalProfiles;
        _terminalProfile.SelectionChanged += (_,_) =>
        {
            ApplyTerminalProfile();
            if (_step == 3) Render();
        };
        Title = "TOR POS – Ersteinrichtung";
        Width = 820; Height = 650; MinWidth = 720; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _back.Click += (_,_) => { if (_step > 0) { _step--; Render(); } };
        _next.Click += async (_,_) => await NextAsync();

        var root = new Grid { Margin = new Thickness(28), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        root.Children.Add(_title); Grid.SetRow(_title, 0);
        root.Children.Add(_stepText); Grid.SetRow(_stepText, 1);
        root.Children.Add(_host); Grid.SetRow(_host, 2);
        root.Children.Add(_status); Grid.SetRow(_status, 3);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Margin = new Thickness(0,12,0,0) };
        buttons.Children.Add(_back); buttons.Children.Add(_next);
        root.Children.Add(buttons); Grid.SetRow(buttons, 4);
        Content = root;
        Opened += async (_,_) => { await LoadAsync(); Render(); };
    }

    private async Task LoadAsync()
    {
        var s = await _settings.LoadAllAsync();
        _company.Text = s.GetValueOrDefault("company.name", ""); _owner.Text = s.GetValueOrDefault("company.owner", "");
        _street.Text = s.GetValueOrDefault("company.street", ""); _zip.Text = s.GetValueOrDefault("company.zip", ""); _city.Text = s.GetValueOrDefault("company.city", "");
        var printers = _printer.GetInstalledPrinterNames().ToList();
        var configured = s.GetValueOrDefault("device.receipt_printer.name", "");
        if (!string.IsNullOrWhiteSpace(configured) && !printers.Contains(configured)) printers.Insert(0, configured);
        _printerName.ItemsSource = printers;
        if (!string.IsNullOrWhiteSpace(configured)) _printerName.SelectedItem = configured; else if (printers.Count > 0) _printerName.SelectedIndex = 0;
        _printerEnabled.IsChecked = !string.Equals(s.GetValueOrDefault("device.receipt_printer.enabled", "true"), "false", StringComparison.OrdinalIgnoreCase);
        var selectedTerminal = PaymentTerminalProfiles.Find(s.GetValueOrDefault("payment.terminal.vendor", "AUTO_ZVT"));
        _terminalProfile.SelectedItem =
            _terminalProfiles.FirstOrDefault(x => x.Profile.Id == selectedTerminal.Id)
            ?? _terminalProfiles.FirstOrDefault();
        _terminalEnabled.IsChecked =
            selectedTerminal.ProductionReady &&
            string.Equals(s.GetValueOrDefault("payment.terminal.enabled", "false"), "true", StringComparison.OrdinalIgnoreCase);
        _terminalIp.Text = s.GetValueOrDefault("payment.terminal.ip", "");
        _terminalPort.Text = s.GetValueOrDefault(
            "payment.terminal.port",
            selectedTerminal.DefaultPort > 0 ? selectedTerminal.DefaultPort.ToString() : "");
        ApplyTerminalProfile();
    }

    private static StackPanel Page(params Control[] controls)
    { var p = new StackPanel { Spacing = 14, Margin = new Thickness(0,24,0,12) }; foreach (var c in controls) p.Children.Add(c); return p; }
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private static Control Field(string label, Control input)
    { var p = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") }; p.Children.Add(new TextBlock { Text=label, VerticalAlignment=VerticalAlignment.Center }); p.Children.Add(input); Grid.SetColumn(input,1); return p; }
    private Button Action(string text, Func<Task> action)
    { var b = new Button { Content=text, Height=46, HorizontalAlignment=HorizontalAlignment.Left, MinWidth=210 }; b.Click += async (_,_) => await action(); return b; }

    private void Render()
    {
        _host.Children.Clear(); _status.Text = ""; _back.IsEnabled = _step > 0; _next.Content = _step == 5 ? "EINRICHTUNG ABSCHLIESSEN" : "WEITER";
        _stepText.Text = $"Schritt {_step + 1} von 6";
        Control page;
        switch (_step)
        {
            case 0:
                _title.Text = "Firma & Kassenart";
                page = Page(Info("Nur die wichtigsten Betriebsdaten. Weitere Bonangaben können später ergänzt werden."),
                    Field("Firma", _company), Field("Inhaber / Betreiber", _owner), Field("Straße", _street), Field("PLZ", _zip), Field("Ort", _city),
                    Info($"Kassenart: {_edition}  ✓  (bei der Anmeldung gewählt)")); break;
            case 1:
                _title.Text = "Bondrucker";
                page = Page(
                    Info("TOR kann verbreitete Epson- und Star-Bondrucker aus Windows-Druckername, Treiber und Port automatisch erkennen. Bei unklarem Modell bleibt die Auswahl manuell."),
                    _printerEnabled,
                    Action("AUTOMATISCH ERKENNEN", AutoDetectPrinterAsync),
                    Field("Drucker", _printerName),
                    Action("DRUCKER PRÜFEN", ProbePrinterAsync),
                    Action("TESTBON DRUCKEN", TestPrinterAsync)); break;
            case 2:
                _title.Text = "TSE";
                page = Page(Info("TOR prüft hier nur die TSE. Es wird keine TSE automatisch aktiviert oder neu eingerichtet."),
                    Info($"Provider: {_tse.DisplayName}"), Action("TSE PRÜFEN", ProbeTseAsync)); break;
            case 3:
            {
                _title.Text = "Kartenterminal";
                ApplyTerminalProfile();
                var profile = SelectedTerminalProfile;
                var controls = new List<Control>
                {
                    Info("Optional. Marke bzw. Terminalfamilie auswählen. TOR zeigt den freigegebenen Integrationsweg und aktiviert keine nicht verifizierte Schnittstelle."),
                    Field("Marke / Profil", _terminalProfile),
                    _terminalProfileHint,
                    _terminalEnabled
                };
                if (profile.RequiresNetworkEndpoint)
                {
                    controls.Add(Field("IP-Adresse", _terminalIp));
                    controls.Add(Field("ZVT TCP-Port", _terminalPort));
                    controls.Add(Action("VERBINDUNG PRÜFEN", ProbeTerminalAsync));
                }
                else if (profile.Id == "SUMUP_CLOUD")
                {
                    controls.Add(Action("SUMUP GERÄT / PAIRING TESTEN", OpenSumUpAsync));
                }
                else
                {
                    controls.Add(Info("Für dieses Profil ist keine IP-/ZVT-Eingabe erforderlich. Die automatische Zahlung bleibt bis zur offiziellen Adapter-/Partnerfreigabe deaktiviert."));
                }
                page = Page(controls.ToArray());
                break;
            }
            case 4:
                _title.Text = "Kontrolle";
                page = Page(Info("Bitte kurz prüfen. Mit WEITER werden die Einstellungen gespeichert."),
                    Info($"Firma: {(_company.Text ?? "").Trim()}\nKassenart: {_edition}\nBondrucker: {(_printerEnabled.IsChecked == true ? (_printerName.SelectedItem?.ToString() ?? "nicht gewählt") : "aus")}\nKartenterminal: {SelectedTerminalProfile.Manufacturer} · {(SelectedTerminalProfile.ProductionReady && _terminalEnabled.IsChecked == true ? "aktiv" : "nicht aktiv")}")); break;
            default:
                _title.Text = "Fertig";
                page = Page(Info("Die Grundeinrichtung ist bereit. Technische Details bleiben unter Einstellungen → Erweitert / Techniker geschützt."),
                    Info("✓ Firma\n✓ Kassenart\n✓ Geräte-Grundeinstellungen\n\nNach Abschluss startet TOR POS normal.")); break;
        }
        _host.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
    }

    private PaymentTerminalProfile SelectedTerminalProfile =>
        (_terminalProfile.SelectedItem as TerminalProfileChoice)?.Profile
        ?? PaymentTerminalProfiles.All[0];

    private void ApplyTerminalProfile()
    {
        var profile = SelectedTerminalProfile;
        _terminalProfileHint.Text =
            $"{profile.Integration} · {profile.TorStatus}\n{profile.Notes}" +
            (string.IsNullOrWhiteSpace(profile.SetupHint) ? "" : $"\nEinrichtung: {profile.SetupHint}");
        _terminalProfileHint.Foreground =
            profile.ProductionReady ? AppTheme.AccentTeal : AppTheme.WarningAmber;
        _terminalEnabled.IsEnabled = profile.ProductionReady;
        if (!profile.ProductionReady)
            _terminalEnabled.IsChecked = false;
        _terminalIp.IsEnabled = profile.RequiresNetworkEndpoint;
        _terminalPort.IsEnabled = profile.RequiresNetworkEndpoint;
        if (profile.RequiresNetworkEndpoint &&
            profile.DefaultPort > 0 &&
            string.IsNullOrWhiteSpace(_terminalPort.Text))
            _terminalPort.Text = profile.DefaultPort.ToString();
    }

    private async Task SaveAsync(bool finish = false)
    {
        var terminalProfile = SelectedTerminalProfile;
        await _settings.SaveManyAsync(new Dictionary<string,string>
        {
            ["company.name"] = (_company.Text ?? "").Trim(), ["company.owner"] = (_owner.Text ?? "").Trim(),
            ["company.street"] = (_street.Text ?? "").Trim(), ["company.zip"] = (_zip.Text ?? "").Trim(), ["company.city"] = (_city.Text ?? "").Trim(),
            ["device.receipt_printer.enabled"] = _printerEnabled.IsChecked == true ? "true" : "false",
            ["device.receipt_printer.name"] = _printerName.SelectedItem?.ToString() ?? "",
            ["payment.terminal.vendor"] = terminalProfile.Id,
            ["payment.terminal.enabled"] = terminalProfile.ProductionReady && _terminalEnabled.IsChecked == true ? "true" : "false",
            ["payment.terminal.ip"] = terminalProfile.RequiresNetworkEndpoint ? (_terminalIp.Text ?? "").Trim() : "",
            ["payment.terminal.port"] = terminalProfile.RequiresNetworkEndpoint ? (_terminalPort.Text ?? terminalProfile.DefaultPort.ToString()).Trim() : "",
            ["payment.terminal.protocol"] = terminalProfile.Protocol,
            ["installation.first_run_completed"] = finish ? "true" : "false"
        });
    }

    private async Task NextAsync()
    {
        if (_step == 0 && string.IsNullOrWhiteSpace(_company.Text)) { _status.Text = "Bitte mindestens den Firmennamen eintragen."; return; }
        if (_step == 4) await SaveAsync();
        if (_step < 5) { _step++; Render(); return; }
        await SaveAsync(true); Completed = true; Close();
    }
    private async Task AutoDetectPrinterAsync()
    {
        try
        {
            _status.Text = "Windows-Drucker werden automatisch geprüft …";
            var devices = await Task.Run(() => _printer.GetInstalledPrinterDevices())
                .WaitAsync(TimeSpan.FromSeconds(12));

            var names = devices.Select(x => x.PrinterName).ToList();
            _printerName.ItemsSource = names;

            var candidates = devices
                .Where(x => x.IsReceiptPrinter && x.Ready != false)
                .OrderByDescending(x => x.ExactModel)
                .ToArray();

            if (candidates.Length == 0)
            {
                if (names.Count > 0) _printerName.SelectedIndex = 0;
                _status.Text = names.Count == 0
                    ? "⚠ Keine Windows-Drucker gefunden. Epson-/Star-Treiber zuerst in Windows installieren."
                    : "⚠ Kein Epson-/Star-Bondrucker eindeutig erkannt. Vorhandene Windows-Drucker wurden geladen; bitte manuell auswählen.";
                return;
            }

            _printerName.SelectedItem = candidates[0].PrinterName;
            if (candidates.Length == 1 && candidates[0].ExactModel)
            {
                var d = candidates[0];
                _status.Text =
                    $"✓ {d.Manufacturer} {d.Model} erkannt · {d.ConnectionType} · {d.PaperWidthMm} mm · {d.PrinterName}";
            }
            else
            {
                _status.Text =
                    $"✓ {candidates.Length} Epson/Star-Bondrucker gefunden. Vorauswahl: {candidates[0].Manufacturer} {candidates[0].Model}. Bitte Auswahl kontrollieren.";
            }
        }
        catch (TimeoutException)
        {
            _status.Text = "⚠ Druckersuche dauert zu lange. Offline-/Netzwerkdrucker in Windows prüfen.";
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ Automatische Druckersuche fehlgeschlagen: " + ex.Message;
        }
    }

    private async Task ProbePrinterAsync() { try { var r = await _printer.ProbeAsync(_printerName.SelectedItem?.ToString() ?? ""); _status.Text = r.Success ? $"✓ {r.Message}" : $"⚠ {r.Message}"; if (r.Success) _printerName.SelectedItem = r.PrinterName; } catch(Exception ex) { _status.Text = "⚠ " + ex.Message; } }
    private async Task TestPrinterAsync() { try { var n=_printerName.SelectedItem?.ToString() ?? ""; if(string.IsNullOrWhiteSpace(n)){_status.Text="Bitte zuerst einen Drucker wählen.";return;} await _printer.PrintTestAsync(n); _status.Text="✓ Testbon gesendet."; } catch(Exception ex){_status.Text="⚠ "+ex.Message;} }
    private async Task ProbeTseAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var probe = _tse.ProbeAsync(timeout.Token);
            var completed = await Task.WhenAny(
                probe,
                Task.Delay(TimeSpan.FromSeconds(10)));

            if (completed != probe)
            {
                _status.Text =
                    "⚠ TSE antwortet nicht innerhalb von 10 Sekunden. USB/SDK prüfen; TOR POS bleibt bedienbar.";
                return;
            }

            var r = await probe;
            var ok = r.State is TseConnectionState.Ready or TseConnectionState.Connected;
            _status.Text = ok ? $"✓ {r.Message}" : $"⚠ {r.Message}";
        }
        catch(Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
        }
    }
    private async Task ProbeTerminalAsync()
    {
        try
        {
            var profile = SelectedTerminalProfile;
            if (!profile.ProductionReady || profile.Protocol != "ZVT_TCP")
            {
                _status.Text = $"⚠ {profile.Manufacturer}: {profile.TorStatus}. Automatische Zahlung ist für dieses Profil noch nicht freigegeben.";
                return;
            }

            await SaveAsync();
            var r = await _terminal.ProbeAsync();
            _status.Text = r.Success ? $"✓ {r.Message}" : $"⚠ {r.Message}";
        }
        catch(Exception ex) { _status.Text = "⚠ " + ex.Message; }
    }

    private async Task OpenSumUpAsync() =>
        await new SumUpConnectionWindow().ShowDialog(this);

    private sealed record TerminalProfileChoice(PaymentTerminalProfile Profile)
    {
        public override string ToString() => $"{Profile.Manufacturer} · {Profile.Family}";
    }
}
