using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// R169: operator-facing printer center. It reads the Windows queue/driver/port,
/// recognizes reviewed Epson/Star receipt-printer families conservatively and
/// keeps the final assignment explicit. Unknown models are never silently
/// converted into a known profile.
/// </summary>
public sealed class PrinterSetupWindow : Window
{
    private readonly ISettingsRepository _settings;
    private readonly IReceiptPrinterService _printer;

    private readonly ComboBox _device = new()
    {
        MinHeight = 44,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        PlaceholderText = "Drucker automatisch suchen"
    };

    private readonly ComboBox _role = new()
    {
        MinHeight = 44,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly TextBlock _model = Value();
    private readonly TextBlock _driver = Value();
    private readonly TextBlock _port = Value();
    private readonly TextBlock _connection = Value();
    private readonly TextBlock _paper = Value();
    private readonly TextBlock _features = Value();
    private readonly TextBlock _recognition = Value();
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 54
    };

    private readonly Button _detect = ActionButton("DRUCKER AUTOMATISCH SUCHEN");
    private readonly Button _use = ActionButton("DIESEN DRUCKER VERWENDEN");
    private readonly Button _test = ActionButton("TESTBON DRUCKEN");
    private readonly Button _drawer = ActionButton("KASSENSCHUBLADE TESTEN");

    private IReadOnlyList<PrinterItem> _items = Array.Empty<PrinterItem>();
    private readonly IReadOnlyList<RoleItem> _roles;

    public PrinterSetupWindow(
        ISettingsRepository settings,
        IReceiptPrinterService printer)
    {
        _settings = settings;
        _printer = printer;

        Title = "TOR POS · Drucker-Zentrale";
        Width = 900;
        Height = 790;
        MinWidth = 740;
        MinHeight = 620;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var roles = new List<RoleItem>
        {
            new("Bondrucker", "device.receipt_printer", DrawerAllowed: true)
        };
        if (InstallationEdition.ReadLocked() == "IMBISS")
        {
            roles.Add(new("Küchendrucker / Standard", "device.kitchen_printer", DrawerAllowed: false));
            roles.Add(new("Küchendrucker · Grill", KitchenStations.SettingsPrefix(KitchenStations.Grill), DrawerAllowed: false));
            roles.Add(new("Küchendrucker · Fritteuse", KitchenStations.SettingsPrefix(KitchenStations.Fritteuse), DrawerAllowed: false));
            roles.Add(new("Küchendrucker · Getränke", KitchenStations.SettingsPrefix(KitchenStations.Getraenke), DrawerAllowed: false));
        }
        _roles = roles;
        _role.ItemsSource = _roles;
        _role.SelectedIndex = 0;

        _device.SelectionChanged += (_, _) => ApplySelection();
        _role.SelectionChanged += (_, _) => ApplySelection();
        _detect.Click += async (_, _) => await DetectAsync();
        _use.Click += async (_, _) => await UseAsync();
        _test.Click += async (_, _) => await TestPrintAsync();
        _drawer.Click += async (_, _) => await TestDrawerAsync();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinHeight = 48,
            MinWidth = 160
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
                                Text = "DRUCKER-ZENTRALE",
                                FontSize = 28,
                                FontWeight = FontWeight.Bold
                            },
                            new TextBlock
                            {
                                Text = "TOR liest Windows-Druckername, Treiber und Port. Epson- und Star-Bondrucker werden nur dann mit einem konkreten Modell bezeichnet, wenn der Modellname eindeutig erkannt wird. Bei unklarem Modell bleibt die Auswahl bewusst manuell.",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Color.Parse("#A7BACC"))
                            },
                            Field("Verwendung", _role),
                            Field("Gefundener Drucker", _device),
                            _detect,
                            DeviceCard(),
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 10,
                                Children = { _use, _test, _drawer }
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
                                Text = "Kassenschubladen-Test: TOR sendet genau einen ESC-p/StarPRNT-Schubladenimpuls über den ausgewählten Bondrucker. Der Test erzeugt keinen Verkauf und keinen Bon. Windows kann nur bestätigen, dass der Befehl an die Druckerwarteschlange übergeben wurde; ob die Schublade physisch geöffnet hat, muss am Gerät kontrolliert werden.",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = AppTheme.WarningAmber
                            }
                        }
                    }
                },
                close
            }
        };
        Grid.SetRow(close, 1);
        close.Margin = new Thickness(22, 8, 22, 18);
        close.HorizontalAlignment = HorizontalAlignment.Right;

        Opened += async (_, _) =>
        {
            await LoadAsync();
            await DetectAsync();
            UiLanguage.Apply(this);
        };
    }

    private PrinterDeviceInfo? SelectedDevice =>
        (_device.SelectedItem as PrinterItem)?.Device;

    private RoleItem SelectedRole =>
        (_role.SelectedItem as RoleItem) ?? _roles[0];

    private async Task LoadAsync()
    {
        var values = await _settings.LoadAllAsync();
        var receiptName = values.GetValueOrDefault("device.receipt_printer.name", "");
        _status.Text = string.IsNullOrWhiteSpace(receiptName)
            ? "Noch kein Bondrucker gespeichert. Automatische Suche startet …"
            : $"Gespeicherter Bondrucker: {receiptName}. Automatische Suche startet …";
    }

    private async Task DetectAsync()
    {
        SetBusy(true);
        try
        {
            _status.Text = "Windows-Drucker werden geprüft …";
            var devices = await Task.Run(() => _printer.GetInstalledPrinterDevices())
                .WaitAsync(TimeSpan.FromSeconds(12));

            _items = devices.Select(x => new PrinterItem(x)).ToArray();
            _device.ItemsSource = _items;

            if (_items.Count == 0)
            {
                _device.SelectedItem = null;
                _status.Text = "Keine Windows-Drucker gefunden. Epson-/Star-Treiber zuerst in Windows installieren.";
                ApplySelection();
                return;
            }

            var values = await _settings.LoadAllAsync();
            var configured = values.GetValueOrDefault(SelectedRole.Prefix + ".name", "");
            var selected = _items.FirstOrDefault(x =>
                string.Equals(x.Device.PrinterName, configured, StringComparison.OrdinalIgnoreCase));

            selected ??= _items
                .Where(x => x.Device.IsReceiptPrinter && x.Device.Ready != false)
                .OrderByDescending(x => x.Device.ExactModel)
                .FirstOrDefault();

            selected ??= _items.First();
            _device.SelectedItem = selected;

            var recognized = _items.Count(x => x.Device.IsReceiptPrinter);
            var exact = _items.Count(x => x.Device.IsReceiptPrinter && x.Device.ExactModel);
            _status.Text =
                $"{_items.Count} Windows-Drucker gefunden · {recognized} Epson/Star-Bondrucker erkannt · {exact} Modell eindeutig. " +
                "Auswahl prüfen, TESTBON DRUCKEN ve danach DIESEN DRUCKER VERWENDEN.";
            ApplySelection();
        }
        catch (TimeoutException)
        {
            _status.Text = "Druckersuche hat länger als 12 Sekunden gedauert. Netzwerk-/Offline-Windows-Drucker prüfen und erneut suchen.";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("R169 printer discovery", ex);
            _status.Text = "Druckersuche fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplySelection()
    {
        var d = SelectedDevice;
        if (d is null)
        {
            _model.Text = "—";
            _driver.Text = "—";
            _port.Text = "—";
            _connection.Text = "—";
            _paper.Text = "—";
            _features.Text = "—";
            _recognition.Text = "—";
            _use.IsEnabled = false;
            _test.IsEnabled = false;
            _drawer.IsEnabled = false;
            return;
        }

        _model.Text = $"{d.Manufacturer} · {d.Model}";
        _driver.Text = string.IsNullOrWhiteSpace(d.DriverName) ? "Windows-Treibername nicht lesbar" : d.DriverName;
        _port.Text = string.IsNullOrWhiteSpace(d.PortName) ? "Windows-Port nicht lesbar" : d.PortName;
        _connection.Text = d.ConnectionType + (d.Ready switch
        {
            true => " · BEREIT",
            false => " · NICHT BEREIT",
            _ => " · Live-Status unbekannt"
        });
        _paper.Text = $"{d.PaperWidthMm} mm";
        _features.Text =
            $"Auto-Cut: {(d.AutoCutSupported ? "Profil unterstützt" : "nicht automatisch bestätigt")} · " +
            $"Schubladenport: {(d.CashDrawerPortSupported ? "Profil unterstützt" : "nicht automatisch bestätigt")}";
        _recognition.Text = (d.ExactModel ? "✓ " : "⚠ ") + d.RecognitionNote;

        _use.IsEnabled = d.Ready != false;
        _test.IsEnabled = d.Ready != false;
        _drawer.IsVisible = SelectedRole.DrawerAllowed;
        _drawer.IsEnabled =
            SelectedRole.DrawerAllowed &&
            d.Ready != false &&
            d.CashDrawerPortSupported;
    }

    private async Task UseAsync()
    {
        var d = SelectedDevice;
        if (d is null)
            return;

        var role = SelectedRole;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [role.Prefix + ".enabled"] = "true",
            [role.Prefix + ".name"] = d.PrinterName,
            [role.Prefix + ".vendor"] = d.Manufacturer,
            [role.Prefix + ".model"] = d.Model,
            [role.Prefix + ".driver"] = d.DriverName,
            [role.Prefix + ".port"] = d.PortName,
            [role.Prefix + ".connection"] = d.ConnectionType,
            [role.Prefix + ".paper_width_mm"] = d.PaperWidthMm.ToString(),
            [role.Prefix + ".profile_exact"] = d.ExactModel ? "true" : "false"
        };

        if (role.Prefix == "device.receipt_printer")
        {
            values["receipt.font_width"] = d.PaperWidthMm <= 58 ? "32" : "42";
            values["device.receipt_printer.profile_autocut"] = d.AutoCutSupported ? "true" : "false";
            values["device.receipt_printer.profile_drawer"] = d.CashDrawerPortSupported ? "true" : "false";
        }

        await _settings.SaveManyAsync(values);
        _status.Text = d.ExactModel
            ? $"✓ {d.Manufacturer} {d.Model} als {role.Title} gespeichert."
            : $"✓ {d.PrinterName} als {role.Title} gespeichert. Modell blieb absichtlich 'nicht eindeutig'; TOR hat kein Modell geraten.";
    }

    private async Task TestPrintAsync()
    {
        var d = SelectedDevice;
        if (d is null)
            return;

        SetBusy(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var probe = await _printer.ProbeAsync(d.PrinterName, timeout.Token);
            if (!probe.Success)
            {
                _status.Text = "⚠ " + probe.Message;
                return;
            }

            await _printer.PrintTestAsync(d.PrinterName, timeout.Token);
            _status.Text = "✓ Testbon an Windows übergeben. Papierausdruck am Gerät kontrollieren.";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("R169 printer test", ex);
            _status.Text = "⚠ Testdruck fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TestDrawerAsync()
    {
        var d = SelectedDevice;
        if (d is null || !SelectedRole.DrawerAllowed)
            return;

        SetBusy(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await _printer.TestCashDrawerAsync(d.PrinterName, timeout.Token);
            _status.Text =
                "✓ Schubladenbefehl an Windows übergeben. Bitte jetzt physisch prüfen, ob die Kassenschublade geöffnet hat. " +
                "TOR wertet das Senden nicht automatisch als bestätigte mechanische Öffnung.";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("R169 drawer test", ex);
            _status.Text = "⚠ Kassenschubladen-Test fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _detect.IsEnabled = !busy;
        _role.IsEnabled = !busy;
        _device.IsEnabled = !busy;
        if (busy)
        {
            _use.IsEnabled = false;
            _test.IsEnabled = false;
            _drawer.IsEnabled = false;
        }
        else
        {
            ApplySelection();
        }
    }

    private Border DeviceCard()
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Row("Hersteller / Modell", _model),
                Row("Windows-Treiber", _driver),
                Row("Port", _port),
                Row("Verbindung / Status", _connection),
                Row("Papierprofil", _paper),
                Row("Funktionen", _features),
                Row("Erkennung", _recognition)
            }
        };

        return new Border
        {
            Background = AppTheme.SurfacePanel,
            BorderBrush = AppTheme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Child = panel
        };
    }

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

    private static Control Row(string label, TextBlock value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*"),
            ColumnSpacing = 12
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.66,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Value() => new()
    {
        Text = "—",
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold
    };

    private static Button ActionButton(string text) => new()
    {
        Content = text,
        MinHeight = 48,
        FontWeight = FontWeight.Bold
    };

    private sealed record PrinterItem(PrinterDeviceInfo Device)
    {
        public override string ToString() =>
            $"{(Device.ExactModel ? "✓" : Device.IsReceiptPrinter ? "?" : "•")} " +
            $"{Device.Manufacturer} {Device.Model} · {Device.ConnectionType} · {Device.PrinterName}";
    }

    private sealed record RoleItem(string Title, string Prefix, bool DrawerAllowed)
    {
        public override string ToString() => Title;
    }
}
