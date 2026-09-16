using System.Linq;
using System.IO;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class TextReportWindow : Window
{
    private readonly BusinessManagementService _management;
    private readonly ReportDocument _document;
    private readonly IReceiptPrinterService? _printer;
    private readonly ISettingsRepository? _settings;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _paperFormat = new() { MinWidth = 150 };
    private readonly ComboBox _printerName = new() { MinWidth = 280 };
    private IReadOnlyDictionary<string,string> _settingsCache =
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

    public TextReportWindow(
        BusinessManagementService management,
        ReportDocument document,
        string? note = null)
        : this(management, document, null, null, note)
    {
    }

    public TextReportWindow(
        BusinessManagementService management,
        ReportDocument document,
        IReceiptPrinterService? printer,
        ISettingsRepository? settings,
        string? note = null)
    {
        _management = management;
        _document = document;
        _printer = printer;
        _settings = settings;

        Title = $"TOR POS Pro - {document.Title}";
        Width = 940;
        Height = 760;
        MinWidth = 720;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Text = string.Join(Environment.NewLine, document.Lines),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MinHeight = 440
        };

        var pdf = new Button
        {
            Content = "PDF SPEICHERN",
            MinHeight = 48,
            MinWidth = 170,
            FontWeight = FontWeight.Bold
        };
        pdf.Click += async (_, _) =>
        {
            try
            {
                pdf.IsEnabled = false;
                var safeName = string.Concat(_document.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "TOR-Bericht";
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Bericht als PDF speichern",
                    SuggestedFileName = safeName + ".pdf",
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } }
                });
                if (file is null) { _status.Text = "PDF-Speichern abgebrochen."; return; }
                var path = await Task.Run(() => _management.SavePdf(_document, file.Path.LocalPath));
                _status.Text = $"PDF gespeichert: {path}";
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Report PDF", ex);
                _status.Text = "PDF-Fehler: " + ex.Message;
            }
            finally { pdf.IsEnabled = true; }
        };

        _paperFormat.ItemsSource = new[]
        {
            new ReportFormatChoice("58 mm · Bondrucker", ReportPaperFormat.Receipt58, "58"),
            new ReportFormatChoice("80 mm · Bondrucker", ReportPaperFormat.Receipt80, "80"),
            new ReportFormatChoice("A4 · Bürodrucker", ReportPaperFormat.A4, "A4")
        };
        _paperFormat.SelectedIndex = 1;
        _paperFormat.SelectionChanged += (_, _) => SelectRecommendedPrinter();

        var print = new Button
        {
            Content = "BERICHT DRUCKEN",
            MinHeight = 48,
            MinWidth = 180,
            FontWeight = FontWeight.Bold
        };
        print.Click += async (_, _) =>
        {
            if (_printer is null || _settings is null)
            {
                _status.Text = "Druckfunktion ist in diesem Fenster nicht verbunden.";
                return;
            }

            if (_paperFormat.SelectedItem is not ReportFormatChoice choice)
            {
                _status.Text = "Bitte Papierformat auswählen.";
                return;
            }

            var printerName = _printerName.SelectedItem?.ToString()?.Trim() ?? "";
            if (printerName.Length == 0)
            {
                _status.Text = "Bitte einen Windows-Drucker auswählen.";
                return;
            }

            try
            {
                print.IsEnabled = false;
                var key = PreferenceKey(_document.Title);
                await _settings.SaveManyAsync(new Dictionary<string,string>
                {
                    [$"report.print.{key}.format"] = choice.Code,
                    [$"report.print.{key}.printer"] = printerName
                });
                await _printer.PrintReportAsync(
                    new ReportPrintJob(
                        _document.Title,
                        _document.Lines,
                        _document.CreatedAt,
                        choice.Format),
                    printerName);
                _status.Text = $"Bericht an „{printerName}“ gesendet · {choice.Label}. Papierausdruck prüfen.";
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Report print", ex);
                _status.Text = "Druckfehler: " + ex.Message;
            }
            finally { print.IsEnabled = true; }
        };

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinHeight = 48,
            MinWidth = 130
        };
        close.Click += (_, _) => Close();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { pdf, print, close }
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = document.Title,
                    FontSize = 24,
                    FontWeight = FontWeight.Bold
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(note))
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#3A3015")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = note,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = AppTheme.WarningAmber
                }
            });
        }

        if (_printer is not null && _settings is not null)
        {
            var printOptions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,170,Auto,*,Auto"),
                ColumnSpacing = 8,
                Margin = new Thickness(0, 2, 0, 2)
            };
            printOptions.Children.Add(new TextBlock
            {
                Text = "Papier:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.Bold
            });
            Grid.SetColumn(_paperFormat, 1);
            printOptions.Children.Add(_paperFormat);
            var printerLabel = new TextBlock
            {
                Text = "Drucker:",
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.Bold
            };
            Grid.SetColumn(printerLabel, 2);
            printOptions.Children.Add(printerLabel);
            Grid.SetColumn(_printerName, 3);
            printOptions.Children.Add(_printerName);
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#142236")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2C4563")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = printOptions
            });

            Opened += async (_, _) => await LoadPrintPreferencesAsync();
        }
        else
        {
            print.IsEnabled = false;
        }

        panel.Children.Add(text);
        panel.Children.Add(_status);
        panel.Children.Add(footer);
        Content = panel;
    }

    private async Task LoadPrintPreferencesAsync()
    {
        if (_printer is null || _settings is null)
            return;

        try
        {
            _settingsCache = await _settings.LoadAllAsync();
            var installed = _printer.GetInstalledPrinterNames();
            _printerName.ItemsSource = installed;

            var key = PreferenceKey(_document.Title);
            var formatCode = _settingsCache.GetValueOrDefault($"report.print.{key}.format", "80");
            var choice = (_paperFormat.ItemsSource as IEnumerable<ReportFormatChoice>)?
                .FirstOrDefault(x => string.Equals(x.Code, formatCode, StringComparison.OrdinalIgnoreCase));
            _paperFormat.SelectedItem = choice ?? (_paperFormat.ItemsSource as IEnumerable<ReportFormatChoice>)?.ElementAtOrDefault(1);

            var preferred = _settingsCache.GetValueOrDefault($"report.print.{key}.printer", "");
            if (installed.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                _printerName.SelectedItem = installed.First(x => string.Equals(x, preferred, StringComparison.OrdinalIgnoreCase));
            else
                SelectRecommendedPrinter();

            _status.Text = installed.Count == 0
                ? "Keine Windows-Drucker gefunden. Unter Einstellungen → Geräte prüfen."
                : "Druckziel auswählen. TOR merkt sich Drucker und Papierformat für diesen Berichtstyp.";
        }
        catch (Exception ex)
        {
            _status.Text = "Druckereinstellungen konnten nicht geladen werden: " + ex.Message;
        }
    }

    private void SelectRecommendedPrinter()
    {
        if (_printer is null || _printerName.ItemsSource is not IEnumerable<string> source)
            return;

        var installed = source.ToArray();
        if (installed.Length == 0)
            return;

        var choice = _paperFormat.SelectedItem as ReportFormatChoice;
        var configured = choice?.Format == ReportPaperFormat.A4
            ? _settingsCache.GetValueOrDefault("device.a4_printer.name", "")
            : _settingsCache.GetValueOrDefault("device.receipt_printer.name", "");

        var match = installed.FirstOrDefault(x =>
            string.Equals(x, configured, StringComparison.OrdinalIgnoreCase));
        _printerName.SelectedItem = match ?? installed[0];
    }

    private static string PreferenceKey(string title)
    {
        var upper = (title ?? "").Trim().ToUpperInvariant();
        if (upper.StartsWith("Z-BERICHT")) return "z";
        if (upper.StartsWith("X-BERICHT")) return "x";
        if (upper.Contains("MONATS")) return "month";
        if (upper.Contains("WARENBESTAND")) return "stock";
        if (upper.Contains("KASSENJOURNAL")) return "cashjournal";
        if (upper.Contains("KASSENSTURZ")) return "cashcount";
        if (upper.Contains("VERKAUFSSTATISTIK")) return "salesstats";
        if (upper.Contains("BEDIENER")) return "operator";
        if (upper.Contains("STORNO")) return "storno";
        if (upper.Contains("UMSATZ")) return "turnover";

        var safe = new string(upper.Where(char.IsLetterOrDigit).Take(24).ToArray());
        return safe.Length == 0 ? "report" : safe.ToLowerInvariant();
    }

    private sealed record ReportFormatChoice(
        string Label,
        ReportPaperFormat Format,
        string Code)
    {
        public override string ToString() => Label;
    }
}

public sealed class DuplicateArticlesWindow : Window
{
    public DuplicateArticlesWindow(IReadOnlyList<DuplicateArticleRow> rows)
    {
        Title = "Duplikate anzeigen";
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new ListBox
        {
            ItemsSource = rows.Count == 0
                ? new[] { "Keine Duplikate gefunden." }
                : rows.Select(x =>
                    $"{x.Kind,-14} | {x.Value} | ID {x.ProductId} | {x.Name} | Art.-Nr. {x.Sku} | EAN {x.Barcode}")
                    .ToArray()
        };

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 46 };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"DUPLIKATE · {rows.Count} Treffer",
                    FontSize = 23,
                    FontWeight = FontWeight.Bold
                },
                list,
                close
            }
        };
        Grid.SetRow(list, 1);
        Grid.SetRow(close, 2);
    }
}

public sealed class InventoryWindow : Window
{
    private readonly BusinessManagementService _management;
    private readonly AuthenticatedUser _user;
    private readonly IReceiptPrinterService _printer;
    private readonly ISettingsRepository _settings;
    private readonly ListBox _list = new();
    private readonly TextBox _quantity = new() { Width = 150, PlaceholderText = "Bestand" };
    private readonly TextBox _search = new()
    {
        MinWidth = 330,
        PlaceholderText = "Name / EAN / Artikel-Nr. · Scanner hier lesen"
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<InventoryArticleRow> _allRows = Array.Empty<InventoryArticleRow>();
    private IReadOnlyList<InventoryArticleRow> _rows = Array.Empty<InventoryArticleRow>();

    public InventoryWindow(
        BusinessManagementService management,
        AuthenticatedUser user,
        IReceiptPrinterService printer,
        ISettingsRepository settings)
    {
        _management = management;
        _user = user;
        _printer = printer;
        _settings = settings;

        Title = "Inventur / Warenbestand";
        Width = 1180;
        Height = 760;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.FontFamily = new FontFamily("Consolas");
        _list.FontSize = 13;
        _list.SelectionChanged += (_, _) =>
        {
            var row = Selected();
            if (row is not null)
                _quantity.Text = row.StockQuantity.ToString("0.###", CultureInfo.GetCultureInfo("de-DE"));
        };

        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindExactBarcode();
                e.Handled = true;
            }
        };
        _quantity.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SaveAsync();
            }
        };

        var searchButton = new Button
        {
            Content = "SUCHEN / SCANNER",
            MinHeight = 42,
            FontWeight = FontWeight.Bold
        };
        searchButton.Click += (_, _) => FindExactBarcode();

        var allButton = new Button { Content = "ALLE ARTIKEL", MinHeight = 42 };
        allButton.Click += (_, _) =>
        {
            _search.Text = "";
            ApplyFilter();
            _search.Focus();
        };

        var searchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _search, searchButton, allButton }
        };

        var save = new Button
        {
            Content = "BESTAND SPEICHERN",
            MinHeight = 46,
            FontWeight = FontWeight.Bold
        };
        save.Click += async (_, _) => await SaveAsync();

        var report = new Button
        {
            Content = "WARENBESTAND · DRUCKEN / PDF",
            MinHeight = 46
        };
        report.Click += async (_, _) =>
        {
            try
            {
                var doc = await _management.BuildInventoryReportAsync();
                await new TextReportWindow(
                    _management,
                    doc,
                    _printer,
                    _settings,
                    "Aktueller Warenbestand. Druckziel kann als 58 mm, 80 mm oder A4 gewählt werden.")
                    .ShowDialog(this);
            }
            catch (Exception ex)
            {
                _status.Text = "Fehler: " + ex.Message;
            }
        };

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 46 };
        close.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Gezählter Bestand:",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeight.Bold
                },
                _quantity,
                save,
                report,
                close
            }
        };

        var header = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "INVENTUR / WARENBESTAND",
                    FontSize = 24,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Gesamte aktive Artikelliste · Scanner-EAN lesen → Bestand eingeben → ENTER. Mindestbestand, EK/VK und Warenwert sind direkt sichtbar.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7
                },
                searchRow,
                new TextBlock
                {
                    Text = "ARTIKEL                 | EAN          | BESTAND / MIN.       | VK       | EK       | WERT EK     | WARENGRUPPE",
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    Opacity = 0.82
                }
            }
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            Children = { header, _list, actions, _status }
        };
        Grid.SetRow(_list, 1);
        Grid.SetRow(actions, 2);
        Grid.SetRow(_status, 3);
        Content = grid;

        Opened += async (_, _) =>
        {
            await ReloadAsync();
            _search.Focus();
        };
    }

    private InventoryArticleRow? Selected() =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count
            ? _rows[_list.SelectedIndex]
            : null;

    private async Task ReloadAsync(long? selectProductId = null)
    {
        _allRows = await _management.GetInventoryAsync();
        ApplyFilter(selectProductId);
    }

    private void ApplyFilter(long? selectProductId = null)
    {
        var query = (_search.Text ?? "").Trim();
        IEnumerable<InventoryArticleRow> rows = _allRows;
        if (query.Length > 0)
        {
            rows = rows.Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Sku.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _rows = rows.ToArray();
        _list.ItemsSource = _rows.Select(InventoryLine).ToArray();
        var low = _allRows.Count(x => x.IsLowStock);
        var purchaseValue = _allRows.Sum(x => x.PurchaseStockValueCents);
        _status.Text = $"{_rows.Count} Artikel angezeigt · {_allRows.Count} aktiv · {low} Mindestbestand-Warnung(en) · Warenwert EK {Formatting.Money(purchaseValue)}.";

        if (_rows.Count == 0)
        {
            _list.SelectedIndex = -1;
            return;
        }

        if (selectProductId is long id)
        {
            var index = _rows.ToList().FindIndex(x => x.ProductId == id);
            _list.SelectedIndex = index >= 0 ? index : 0;
        }
        else
        {
            _list.SelectedIndex = 0;
        }
    }

    private void FindExactBarcode()
    {
        var code = (_search.Text ?? "").Trim();
        if (code.Length == 0)
        {
            _search.Focus();
            return;
        }

        var exact = _allRows.FirstOrDefault(x =>
            string.Equals(x.Barcode, code, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Sku, code, StringComparison.OrdinalIgnoreCase));
        if (exact is null)
        {
            ApplyFilter();
            _status.Text = _rows.Count == 0
                ? $"Kein Artikel gefunden: {code}"
                : $"{_rows.Count} Treffer für „{code}“ · bitte Artikel auswählen.";
            return;
        }

        _rows = new[] { exact };
        _list.ItemsSource = new[] { InventoryLine(exact) };
        _list.SelectedIndex = 0;
        _quantity.Focus();
        _quantity.SelectAll();
        _status.Text = $"Scanner-Treffer: {exact.Name} · Bestand {exact.StockQuantity:0.###} {exact.Unit}" +
            (exact.MinStockQuantity > 0m ? $" · Mindestbestand {exact.MinStockQuantity:0.###}" : "") +
            " · neuen Bestand eingeben und ENTER drücken.";
    }

    private async Task SaveAsync()
    {
        var row = Selected();
        if (row is null)
        {
            _status.Text = "Bitte einen Artikel auswählen.";
            return;
        }

        var raw = (_quantity.Text ?? "").Trim().Replace(',', '.');
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) || qty < 0)
        {
            _status.Text = "Ungültiger Bestand.";
            return;
        }

        var saved = await _management.TrySetInventoryAsync(
            row.ProductId,
            row.StockQuantity,
            qty,
            _user.Username);

        if (!saved)
        {
            await ReloadAsync(row.ProductId);
            _status.Text = "Bestand wurde seit dem Öffnen an anderer Stelle verändert. TOR hat den neueren Bestand NICHT überschrieben. Bitte aktuellen Wert prüfen und erneut speichern.";
            return;
        }

        await ReloadAsync(row.ProductId);
        App.CloudSync?.RequestStockRefresh();
        _status.Text = $"{row.Name}: Bestand {qty:0.###} gespeichert · Cloud-Abgleich vorgemerkt · nächsten Barcode scannen.";
        _search.Focus();
        _search.SelectAll();
    }

    private static string InventoryLine(InventoryArticleRow x) =>
        $"{(x.IsLowStock ? "⚠ " : "  ")}{ClipText(x.Name, 20),-20} | {ClipText(x.Barcode, 12),-12} | " +
        $"{($"{x.StockQuantity:0.###}/{x.MinStockQuantity:0.###} {x.Unit}"),-20} | " +
        $"{GermanFormat.Amount(x.PriceCents)} € | {GermanFormat.Amount(x.PurchasePriceCents)} € | " +
        $"{GermanFormat.Amount(x.PurchaseStockValueCents)} € | {ClipText(x.CategoryName, 18)}";

    private static string ClipText(string value, int max)
    {
        value ??= "";
        return value.Length <= max ? value : value[..Math.Max(1, max - 1)] + "…";
    }
}

public sealed class ZArchiveWindow : Window
{
    private readonly BusinessManagementService _management;
    private readonly IReceiptPrinterService _printer;
    private readonly ISettingsRepository _settings;
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<ZArchiveRow> _rows = Array.Empty<ZArchiveRow>();

    public ZArchiveWindow(
        BusinessManagementService management,
        IReceiptPrinterService printer,
        ISettingsRepository settings)
    {
        _management = management;
        _printer = printer;
        _settings = settings;
        Title = "Z-Abschluss-Journal";
        Width = 920;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var show = new Button { Content = "ANZEIGEN", MinHeight = 46 };
        show.Click += async (_, _) => await ShowSelectedAsync(false);

        var print = new Button
        {
            Content = "Z-BERICHT PDF / DRUCKEN",
            MinHeight = 46,
            FontWeight = FontWeight.Bold
        };
        print.Click += async (_, _) => await ShowSelectedAsync(true);

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 46 };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { show, print, close }
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(18),
            RowSpacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Z-ABSCHLUSS-JOURNAL",
                    FontSize = 24,
                    FontWeight = FontWeight.Bold
                },
                _list,
                buttons,
                _status
            }
        };
        Grid.SetRow(_list, 1);
        Grid.SetRow(buttons, 2);
        Grid.SetRow(_status, 3);
        Content = grid;

        Opened += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _rows = await _management.GetZArchiveAsync();
        _list.ItemsSource = _rows.Count == 0
            ? new[] { "Noch keine archivierten Z-Berichte vorhanden." }
            : _rows.Select(x =>
                $"Z {x.ZNumber:000000} | {x.CreatedAt:dd.MM.yyyy HH:mm} | {x.ReceiptCount} Bons | {GermanFormat.Amount(x.GrossCents)} € | {x.OperatorName} | {x.FiscalStatus}")
                .ToArray();
        if (_rows.Count > 0)
            _list.SelectedIndex = 0;
    }

    private ZArchiveRow? Selected() =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count
            ? _rows[_list.SelectedIndex]
            : null;

    private async Task ShowSelectedAsync(bool createPdf)
    {
        var row = Selected();
        if (row is null)
        {
            _status.Text = "Bitte einen archivierten Z-Bericht auswählen.";
            return;
        }

        var document = _management.ZArchiveToDocument(row);
        await new TextReportWindow(
            _management,
            document,
            _printer,
            _settings,
            "Nachdruck aus dem unveränderbaren Z-Archiv. Der ursprüngliche Z-Datensatz wird nicht geändert. Papierformat und Drucker sind frei wählbar.")
            .ShowDialog(this);
        _status.Text = createPdf
            ? $"Z {row.ZNumber:000000}: Druck-/PDF-Fenster geöffnet. Archivdaten bleiben unverändert."
            : $"Z {row.ZNumber:000000} angezeigt.";
    }
}

public sealed class PfandLeergutSettingsWindow : Window
{
    private readonly ISettingsRepository _settings;
    private readonly TextBox _p8 = Box();
    private readonly TextBox _p15 = Box();
    private readonly TextBox _p25 = Box();
    private readonly TextBox _empty = Box();
    private readonly TextBox _full = Box();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public PfandLeergutSettingsWindow(ISettingsRepository settings)
    {
        _settings = settings;
        Title = "Pfand / Leergut";
        Width = 560;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button
        {
            Content = "SPEICHERN",
            MinHeight = 50,
            FontWeight = FontWeight.Bold
        };
        save.Click += async (_, _) => await SaveAsync();

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 50 };
        close.Click += (_, _) => Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "PFAND / LEERGUT", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = "Direkte Pfand-Tasten der KIOSK-Version. Werte in Cent.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = .68
                }
            }
        };

        AddField(panel, "Pfand 8 Cent", _p8);
        AddField(panel, "Pfand 15 Cent", _p15);
        AddField(panel, "Pfand 25 Cent", _p25);
        AddField(panel, "Leergut Kiste leer", _empty);
        AddField(panel, "Leergut Kiste voll", _full);
        panel.Children.Add(_status);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { save, close }
        });

        Content = panel;
        Opened += async (_, _) => await LoadAsync();
    }

    private static TextBox Box() => new() { Width = 160, MinHeight = 40 };

    private static void AddField(StackPanel panel, string label, TextBox box)
    {
        panel.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeight.SemiBold
                },
                box
            }
        });
        Grid.SetColumn(box, 1);
    }

    private async Task LoadAsync()
    {
        var s = await _settings.LoadAllAsync();
        _p8.Text = s.GetValueOrDefault("pfand.direct.8.cent", "8");
        _p15.Text = s.GetValueOrDefault("pfand.direct.15.cent", "15");
        _p25.Text = s.GetValueOrDefault("pfand.direct.25.cent", "25");
        _empty.Text = s.GetValueOrDefault("pfand.crate.empty.cent", "150");
        _full.Text = s.GetValueOrDefault("pfand.crate.full.cent", "330");
    }

    private async Task SaveAsync()
    {
        var boxes = new[] { _p8, _p15, _p25, _empty, _full };
        if (boxes.Any(x => !long.TryParse(x.Text, out var v) || v < 0 || v > 100000))
        {
            _status.Text = "Bitte gültige Cent-Beträge eingeben.";
            return;
        }

        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["pfand.direct.8.cent"] = _p8.Text!.Trim(),
            ["pfand.direct.15.cent"] = _p15.Text!.Trim(),
            ["pfand.direct.25.cent"] = _p25.Text!.Trim(),
            ["pfand.crate.empty.cent"] = _empty.Text!.Trim(),
            ["pfand.crate.full.cent"] = _full.Text!.Trim()
        });
        _status.Text = "Pfand-/Leergutwerte gespeichert.";
    }
}
