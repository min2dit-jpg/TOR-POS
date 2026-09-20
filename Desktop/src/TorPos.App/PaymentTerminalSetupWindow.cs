using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// R167: operator-facing terminal assistant. It deliberately separates
/// "brand is listed" from "automatic card charging is production-ready".
/// Only reviewed ZVT/TCP profiles can be enabled for live checkout today.
/// Proprietary providers remain visible with their exact onboarding status
/// instead of being silently treated as ZVT.
/// </summary>
public sealed class PaymentTerminalSetupWindow : Window
{
    private readonly ISettingsRepository _settings;
    private readonly IPaymentTerminalService _terminal;
    private readonly ComboBox _profile = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _enabled = new() { Content = "Automatische Kartenterminal-Anbindung aktivieren", MinHeight = 38 };
    private readonly TextBox _model = new() { MinHeight = 42, PlaceholderText = "Modell / eigene Notiz (optional)" };
    private readonly TextBox _ip = new() { MinHeight = 42, PlaceholderText = "z. B. 192.168.1.50" };
    private readonly TextBox _port = new() { MinHeight = 42, Text = "20007" };
    private readonly Border _networkBox = new();
    private readonly TextBlock _headline = new() { FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _integration = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 46 };
    private readonly Button _save = new() { Content = "PROFIL SPEICHERN", MinHeight = 48, FontWeight = FontWeight.Bold };
    private readonly Button _probe = new() { Content = "VERBINDUNG TESTEN", MinHeight = 48, FontWeight = FontWeight.Bold };
    private readonly Button _register = new() { Content = "ZVT ANMELDUNG TESTEN", MinHeight = 48 };
    private readonly Button _sumUp = new() { Content = "SUMUP GERÄT / PAIRING ÖFFNEN", MinHeight = 48 };
    private IReadOnlyList<ProfileItem> _items = Array.Empty<ProfileItem>();

    public PaymentTerminalSetupWindow(
        ISettingsRepository settings,
        IPaymentTerminalService terminal)
    {
        _settings = settings;
        _terminal = terminal;

        Title = "TOR POS · Kartenterminal-Assistent";
        Width = 860;
        Height = 760;
        MinWidth = 720;
        MinHeight = 620;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        _items = terminal.Profiles
            .Select(x => new ProfileItem(x))
            .ToArray();
        _profile.ItemsSource = _items;
        _profile.SelectionChanged += (_, _) => ApplySelectedProfile();

        _networkBox = new Border
        {
            Background = AppTheme.SurfacePanel,
            BorderBrush = AppTheme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    SectionTitle("ZVT · NETZWERK"),
                    Field("IP-Adresse", _ip),
                    Field("TCP-Port", _port),
                    new TextBlock
                    {
                        Text = "IP und Port stehen je nach Anbieter im Terminalmenü. Häufig ist der ZVT-Port 20007; maßgeblich ist immer die tatsächliche Terminalkonfiguration.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.70
                    }
                }
            }
        };

        _save.Click += async (_, _) => await SaveAsync(showConfirmation: true);
        _probe.Click += async (_, _) => await ProbeAsync();
        _register.Click += async (_, _) => await RegisterAsync();
        _sumUp.Click += async (_, _) => await new SumUpConnectionWindow().ShowDialog(this);

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinHeight = 48,
            MinWidth = 150
        };
        close.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "KARTENTERMINAL VERBINDEN",
                        FontSize = 28,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = "Marke bzw. Terminalfamilie auswählen. TOR zeigt nur die Angaben, die für diesen Integrationsweg benötigt werden. Ein gelisteter Hersteller bedeutet nicht automatisch, dass dessen proprietäre Schnittstelle ohne Providerfreigabe verwendet werden darf.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#A7BACC"))
                    },
                    Field("Marke / Profil", _profile),
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#102235")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(14),
                        Child = new StackPanel
                        {
                            Spacing = 7,
                            Children = { _headline, _integration, _hint }
                        }
                    },
                    Field("Modell / Notiz", _model),
                    _networkBox,
                    _enabled,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { _save, _probe, _register, _sumUp }
                    },
                    new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#111F30")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#294765")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(12),
                        Child = _status
                    },
                    new TextBlock
                    {
                        Text = "Sicherheitsregel: TOR POS speichert keine vollständige Kartennummer, keine PIN und keinen CVV/CVC. Nicht freigegebene Providerprofile bleiben automatisch deaktiviert; TOR startet darüber keine vermeintliche Zahlung.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = AppTheme.AccentTeal
                    },
                    close
                }
            }
        };

        Opened += async (_, _) =>
        {
            await LoadAsync();
            UiLanguage.Apply(this);
        };
    }

    private PaymentTerminalProfile SelectedProfile =>
        (_profile.SelectedItem as ProfileItem)?.Profile
        ?? PaymentTerminalProfiles.All[0];

    private async Task LoadAsync()
    {
        var values = await _settings.LoadAllAsync();
        var selected = PaymentTerminalProfiles.Find(values.GetValueOrDefault("payment.terminal.vendor"));
        _profile.SelectedItem = _items.FirstOrDefault(x => x.Profile.Id == selected.Id) ?? _items[0];
        _model.Text = values.GetValueOrDefault("payment.terminal.model", "");
        _ip.Text = values.GetValueOrDefault("payment.terminal.ip", "");
        _port.Text = values.GetValueOrDefault("payment.terminal.port", selected.DefaultPort > 0 ? selected.DefaultPort.ToString() : "");
        _enabled.IsChecked =
            selected.ProductionReady &&
            string.Equals(values.GetValueOrDefault("payment.terminal.enabled"), "true", StringComparison.OrdinalIgnoreCase);
        ApplySelectedProfile();
        _status.Text = "Profil auswählen, Angaben eintragen und SPEICHERN. ZVT-Profile können danach ohne Zahlung getestet werden.";
    }

    private void ApplySelectedProfile()
    {
        var p = SelectedProfile;
        _headline.Text = $"{p.Manufacturer} · {p.Family}";
        _integration.Text = $"{p.Integration} · {p.TorStatus}";
        _integration.Foreground = p.ProductionReady ? AppTheme.AccentTeal : AppTheme.WarningAmber;
        _hint.Text = string.IsNullOrWhiteSpace(p.SetupHint)
            ? p.Notes
            : p.Notes + "\n\nEinrichtung: " + p.SetupHint;

        _networkBox.IsVisible = p.RequiresNetworkEndpoint;
        _probe.IsVisible = p.ProductionReady && p.Protocol == "ZVT_TCP";
        _register.IsVisible = p.ProductionReady && p.Protocol == "ZVT_TCP";
        _sumUp.IsVisible = p.Id == "SUMUP_CLOUD";

        _enabled.IsEnabled = p.ProductionReady;
        if (!p.ProductionReady)
            _enabled.IsChecked = false;

        if (p.RequiresNetworkEndpoint && p.DefaultPort > 0 &&
            (string.IsNullOrWhiteSpace(_port.Text) || _port.Text == "0"))
        {
            _port.Text = p.DefaultPort.ToString();
        }

        if (!p.ProductionReady)
        {
            _status.Text =
                $"{p.Manufacturer}: Profil ist vorbereitet, aber automatische Belastung ist noch nicht freigegeben. " +
                "TOR lässt dieses Profil deshalb absichtlich deaktiviert.";
        }
        else
        {
            _status.Text =
                $"{p.Manufacturer}: ZVT-Profil kann produktiv verwendet werden, sobald Provider/Terminal ZVT freigeschaltet hat und der Verbindungstest erfolgreich ist.";
        }
    }

    private async Task SaveAsync(bool showConfirmation)
    {
        var p = SelectedProfile;
        if (p.RequiresNetworkEndpoint && _enabled.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(_ip.Text))
                throw new InvalidOperationException("IP-Adresse des Terminals fehlt.");
            if (!int.TryParse((_port.Text ?? "").Trim(), out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("Gültigen TCP-Port zwischen 1 und 65535 eingeben.");
        }

        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["payment.terminal.vendor"] = p.Id,
            ["payment.terminal.protocol"] = p.Protocol,
            ["payment.terminal.enabled"] = (p.ProductionReady && _enabled.IsChecked == true) ? "true" : "false",
            ["payment.terminal.model"] = (_model.Text ?? "").Trim(),
            ["payment.terminal.ip"] = p.RequiresNetworkEndpoint ? (_ip.Text ?? "").Trim() : "",
            ["payment.terminal.port"] = p.RequiresNetworkEndpoint
                ? ((_port.Text ?? "").Trim().Length == 0 ? p.DefaultPort.ToString() : (_port.Text ?? "").Trim())
                : "",
            ["payment.terminal.connect_timeout_seconds"] = "5",
            ["payment.terminal.command_timeout_seconds"] = "120",
            ["payment.terminal.register_before_payment"] = "true"
        });

        if (showConfirmation)
            _status.Text = p.ProductionReady
                ? $"✓ {p.Manufacturer}-Profil gespeichert."
                : $"✓ {p.Manufacturer}-Profil vorgemerkt. Automatische Zahlung bleibt bis zur Adapter-/Partnerfreigabe AUS.";
    }

    private async Task ProbeAsync()
    {
        try
        {
            await SaveAsync(showConfirmation: false);
            _status.Text = "Verbindung wird geprüft …";
            var result = await _terminal.ProbeAsync();
            _status.Text = result.Success ? "✓ " + result.Message : "⚠ " + result.Message;
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            await SaveAsync(showConfirmation: false);
            _status.Text = "ZVT-Anmeldung wird geprüft …";
            var result = await _terminal.RegisterAsync();
            _status.Text = result.Success ? "✓ " + result.Message : "⚠ " + result.Message;
        }
        catch (Exception ex)
        {
            _status.Text = "⚠ " + ex.Message;
        }
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        Foreground = AppTheme.AccentTeal
    };

    private static Control Field(string label, Control input)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 12
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72
        });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private sealed record ProfileItem(PaymentTerminalProfile Profile)
    {
        public override string ToString() =>
            $"{Profile.Manufacturer} · {Profile.Family}";
    }
}
