using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// R170 configuration surface for scales. Manual weighed sales work independently
/// from this window. Connected scale modes are stored fail-closed until a matching
/// device protocol/adapter is available.
/// </summary>
public sealed class ScaleSetupWindow : Window
{
    private readonly ISettingsRepository _settings;

    private readonly ComboBox _mode = new()
    {
        MinHeight = 44,
        ItemsSource = new[]
        {
            "MANUELL · keine Verbindung",
            "SERIELL / USB-COM",
            "LAN / TCP",
            "WAAGEN-BARCODE / ETIKETT"
        },
        SelectedIndex = 0
    };
    private readonly TextBox _manufacturer = new() { PlaceholderText = "z. B. Bizerba, Mettler Toledo, CAS" };
    private readonly TextBox _model = new() { PlaceholderText = "Modellbezeichnung" };
    private readonly TextBox _comPort = new() { PlaceholderText = "z. B. COM3" };
    private readonly TextBox _baud = new() { Text = "9600" };
    private readonly TextBox _ip = new() { PlaceholderText = "z. B. 192.168.1.60" };
    private readonly TextBox _tcpPort = new() { Text = "8000" };
    private readonly TextBox _barcodePrefix = new() { PlaceholderText = "z. B. 21" };
    private readonly ComboBox _barcodePayload = new()
    {
        ItemsSource = new[] { "Gewicht in Gramm", "Preis in Cent" },
        SelectedIndex = 0
    };
    private readonly CheckBox _manualFallback = new()
    {
        Content = "Manuelle Gewichtseingabe immer erlauben",
        IsChecked = true
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 48
    };

    private readonly StackPanel _serial = new() { Spacing = 10 };
    private readonly StackPanel _tcp = new() { Spacing = 10 };
    private readonly StackPanel _barcode = new() { Spacing = 10 };

    public ScaleSetupWindow(ISettingsRepository settings)
    {
        _settings = settings;
        Title = "TOR POS · Waagen-Einstellungen";
        Width = 800;
        Height = 760;
        MinWidth = 680;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _serial.Children.Add(Field("COM-Port", _comPort));
        _serial.Children.Add(Field("Baudrate", _baud));

        _tcp.Children.Add(Field("IP-Adresse", _ip));
        _tcp.Children.Add(Field("TCP-Port", _tcpPort));

        _barcode.Children.Add(Field("EAN-Präfix", _barcodePrefix));
        _barcode.Children.Add(Field("Inhalt", _barcodePayload));

        _mode.SelectionChanged += (_, _) => ApplyMode();

        var validate = new Button
        {
            Content = "KONFIGURATION PRÜFEN",
            MinHeight = 48,
            FontWeight = FontWeight.Bold
        };
        validate.Click += (_, _) => ValidateConfiguration();

        var save = new Button
        {
            Content = "WAAGEN-EINSTELLUNGEN SPEICHERN",
            MinHeight = 52,
            FontWeight = FontWeight.Bold,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder
        };
        save.Click += async (_, _) => await SaveAsync();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinHeight = 48
        };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer
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
                                Text = "WAAGE / GEWICHTSVERKAUF",
                                FontSize = 28,
                                FontWeight = FontWeight.Bold
                            },
                            new TextBlock
                            {
                                Text = "Manueller Gewichtsverkauf funktioniert immer ohne Kassenanschluss: Gewicht an einer separaten Waage ablesen und beim Artikel in g oder kg eingeben. Diese Einstellungen bereiten zusätzlich angeschlossene Waagen bzw. Waagen-Barcodes vor.",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = AppTheme.TextMuted
                            },
                            Field("Betriebsart", _mode),
                            Field("Hersteller", _manufacturer),
                            Field("Modell", _model),
                            _serial,
                            _tcp,
                            _barcode,
                            _manualFallback,
                            validate,
                            new Border
                            {
                                Background = AppTheme.SurfacePanel,
                                BorderBrush = AppTheme.PanelBorder,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(9),
                                Padding = new Thickness(12),
                                Child = _status
                            },
                            new TextBlock
                            {
                                Text = "Hinweis: SERIELL/LAN speichert die Geräteparameter, aktiviert aber ohne freigegebenes Herstellerprotokoll keinen automatischen Gewichtsempfang. TOR gibt deshalb keinen erfolgreichen Gerätetest vor, wenn nur IP/COM konfiguriert ist.",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = AppTheme.WarningAmber
                            }
                        }
                    }
                },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    ColumnSpacing = 12,
                    Margin = new Thickness(22,8,22,18),
                    Children = { close, save }
                }
            }
        };
        Grid.SetRow(((Grid)Content).Children[1], 1);
        Grid.SetColumn(save, 1);

        Opened += async (_, _) =>
        {
            await LoadAsync();
            ApplyMode();
            UiLanguage.Apply(this);
        };
    }

    private string ModeId => _mode.SelectedIndex switch
    {
        1 => "SERIAL",
        2 => "TCP",
        3 => "BARCODE",
        _ => "MANUAL"
    };

    private void ApplyMode()
    {
        _serial.IsVisible = ModeId == "SERIAL";
        _tcp.IsVisible = ModeId == "TCP";
        _barcode.IsVisible = ModeId == "BARCODE";
        if (ModeId == "MANUAL")
            _status.Text = UiLanguage.T("✓ Manuelle Waage: keine Verbindung erforderlich. Gewichtsartikel können sofort in g oder kg erfasst werden.");
        else
            _status.Text = UiLanguage.T("Geräteparameter eingeben und KONFIGURATION PRÜFEN wählen.");
    }

    private async Task LoadAsync()
    {
        var all = await _settings.LoadAllAsync();
        _mode.SelectedIndex = all.GetValueOrDefault("scale.mode", "MANUAL").ToUpperInvariant() switch
        {
            "SERIAL" => 1,
            "TCP" => 2,
            "BARCODE" => 3,
            _ => 0
        };
        _manufacturer.Text = all.GetValueOrDefault("scale.manufacturer", "");
        _model.Text = all.GetValueOrDefault("scale.model", "");
        _comPort.Text = all.GetValueOrDefault("scale.serial.port", "");
        _baud.Text = all.GetValueOrDefault("scale.serial.baud", "9600");
        _ip.Text = all.GetValueOrDefault("scale.tcp.ip", "");
        _tcpPort.Text = all.GetValueOrDefault("scale.tcp.port", "8000");
        _barcodePrefix.Text = all.GetValueOrDefault("scale.barcode.prefix", "");
        _barcodePayload.SelectedIndex =
            string.Equals(all.GetValueOrDefault("scale.barcode.payload", "WEIGHT_GRAMS"), "PRICE_CENTS", StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;
        _manualFallback.IsChecked =
            !string.Equals(all.GetValueOrDefault("scale.manual_fallback", "true"), "false", StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateConfiguration()
    {
        if (_manualFallback.IsChecked != true && ModeId == "MANUAL")
        {
            _status.Text = UiLanguage.T("⚠ Bei MANUELL muss die manuelle Gewichtseingabe aktiviert bleiben.");
            return false;
        }

        switch (ModeId)
        {
            case "SERIAL":
                if (string.IsNullOrWhiteSpace(_comPort.Text) ||
                    !int.TryParse(_baud.Text, out var baud) || baud < 1200 || baud > 921600)
                {
                    _status.Text = UiLanguage.T("⚠ COM-Port und gültige Baudrate angeben.");
                    return false;
                }
                break;
            case "TCP":
                if (string.IsNullOrWhiteSpace(_ip.Text) ||
                    !int.TryParse(_tcpPort.Text, out var port) || port is < 1 or > 65535)
                {
                    _status.Text = UiLanguage.T("⚠ IP-Adresse und gültigen TCP-Port angeben.");
                    return false;
                }
                break;
            case "BARCODE":
                var prefix = (_barcodePrefix.Text ?? "").Trim();
                if (prefix.Length is < 1 or > 4 || prefix.Any(ch => !char.IsDigit(ch)))
                {
                    _status.Text = UiLanguage.T("⚠ Waagenbarcode-Präfix muss 1–4 Ziffern enthalten.");
                    return false;
                }
                break;
        }

        _status.Text = UiLanguage.T(ModeId == "MANUAL"
            ? "✓ Manuelle Gewichtseingabe ist betriebsbereit."
            : "✓ Konfiguration formal gültig. Ein echter Live-Gerätetest wird erst mit dem freigegebenen Protokoll/Adapter durchgeführt.");
        return true;
    }

    private async Task SaveAsync()
    {
        if (!ValidateConfiguration())
            return;

        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["scale.mode"] = ModeId,
            ["scale.manufacturer"] = (_manufacturer.Text ?? "").Trim(),
            ["scale.model"] = (_model.Text ?? "").Trim(),
            ["scale.serial.port"] = (_comPort.Text ?? "").Trim().ToUpperInvariant(),
            ["scale.serial.baud"] = (_baud.Text ?? "9600").Trim(),
            ["scale.tcp.ip"] = (_ip.Text ?? "").Trim(),
            ["scale.tcp.port"] = (_tcpPort.Text ?? "8000").Trim(),
            ["scale.barcode.prefix"] = (_barcodePrefix.Text ?? "").Trim(),
            ["scale.barcode.payload"] = _barcodePayload.SelectedIndex == 1 ? "PRICE_CENTS" : "WEIGHT_GRAMS",
            ["scale.manual_fallback"] = _manualFallback.IsChecked == true ? "true" : "false"
        });

        _status.Text = UiLanguage.T(ModeId == "MANUAL"
            ? "✓ Gespeichert. Separate Waage ablesen → Gewicht am Kassenartikel manuell eingeben."
            : "✓ Waagenparameter gespeichert. Manuelle Eingabe bleibt als sichere Rückfallebene verfügbar.");
    }

    private static Control Field(string label, Control input)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("210,*"),
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
}
