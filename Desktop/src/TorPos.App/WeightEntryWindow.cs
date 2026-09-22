using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed record WeightEntryResult(decimal Kilograms);

/// <summary>
/// R170 manual weight capture. Works without any connected scale. A future
/// scale adapter can feed the same kilogram result without changing SaleEngine.
/// </summary>
public sealed class WeightEntryWindow : Window
{
    private readonly TextBox _value = new()
    {
        Text = "500",
        MinHeight = 48,
        FontSize = 24,
        HorizontalContentAlignment = HorizontalAlignment.Right
    };

    private readonly ComboBox _unit = new()
    {
        MinHeight = 48,
        MinWidth = 150,
        ItemsSource = new[] { "g", "kg" },
        SelectedIndex = 0
    };

    private readonly TextBlock _preview = new()
    {
        FontSize = 22,
        FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.WarningAmber
    };

    private readonly Product _product;

    public WeightEntryWindow(Product product)
    {
        _product = product ?? throw new ArgumentNullException(nameof(product));
        if (!_product.IsWeighted)
            throw new InvalidOperationException("Der Artikel ist kein Gewichtsartikel.");

        Title = "TOR POS · Gewicht eingeben";
        Width = 650;
        Height = 590;
        MinWidth = 560;
        MinHeight = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _value.TextChanged += (_, _) => RefreshPreview();
        _unit.SelectionChanged += (_, _) => RefreshPreview();

        Button Quick(string text, decimal value, string unit)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 120,
                MinHeight = 58,
                FontSize = 18,
                FontWeight = FontWeight.Bold
            };
            b.Click += (_, _) =>
            {
                _value.Text = value.ToString(
                    "0.###",
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
                _unit.SelectedItem = unit;
                RefreshPreview();
            };
            return b;
        }

        var accept = new Button
        {
            Content = "ÜBERNEHMEN",
            MinHeight = 58,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder
        };
        accept.Click += (_, _) =>
        {
            if (!TryRead(out var kg))
                return;
            Close(new WeightEntryResult(kg));
        };

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 58,
            MinWidth = 170
        };
        cancel.Click += (_, _) => Close(null);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "GEWICHTSARTIKEL",
                        FontSize = 28,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = _product.Name,
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"{Formatting.Money(_product.BasePriceCents)} / kg",
                        FontSize = 20,
                        Foreground = AppTheme.AccentTeal
                    },
                    new TextBlock
                    {
                        Text = "Funktioniert auch ohne angeschlossene Waage: Gewicht ablesen, hier in Gramm oder Kilogramm eingeben und übernehmen.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.76
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,160"),
                        ColumnSpacing = 12,
                        Children = { _value, _unit }
                    },
                    new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        ItemWidth = 130,
                        ItemHeight = 62,
                        Children =
                        {
                            Quick("100 g", 100m, "g"),
                            Quick("250 g", 250m, "g"),
                            Quick("500 g", 500m, "g"),
                            Quick("1 kg", 1m, "kg")
                        }
                    },
                    new Border
                    {
                        Background = AppTheme.SurfacePanel,
                        BorderBrush = AppTheme.PanelBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(14),
                        Child = _preview
                    },
                    _status,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,*"),
                        ColumnSpacing = 12,
                        Children = { cancel, accept }
                    }
                }
            }
        };
        Grid.SetColumn(accept, 1);

        Opened += (_, _) =>
        {
            RefreshPreview();
            _value.Focus();
            _value.SelectAll();
            UiLanguage.Apply(this);
        };
    }

    private bool TryRead(out decimal kilograms)
    {
        kilograms = 0m;
        var raw = (_value.Text ?? "").Trim().Replace(',', '.');
        if (!decimal.TryParse(
                raw,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0m)
        {
            _status.Text = UiLanguage.T("Bitte ein Gewicht größer als 0 eingeben.");
            return false;
        }

        try
        {
            kilograms = WeightedSales.ToKilograms(
                value,
                string.Equals(_unit.SelectedItem?.ToString(), "kg", StringComparison.OrdinalIgnoreCase)
                    ? WeightInputUnit.Kilogram
                    : WeightInputUnit.Gram);
            _status.Text = "";
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return false;
        }
    }

    private void RefreshPreview()
    {
        if (!TryRead(out var kg))
        {
            _preview.Text = "Gewicht eingeben";
            return;
        }

        var total = WeightedSales.TotalCents(_product.BasePriceCents, kg);
        _preview.Text =
            $"{WeightedSales.QuantityLabel(kg)} × {Formatting.Money(_product.BasePriceCents)} / kg = {Formatting.Money(total)}";
    }
}
