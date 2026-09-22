using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TorPos.App;

public sealed class EditionSelectionWindow : Window
{
    public EditionSelectionWindow()
    {
        Opened += (_, _) => UiLanguage.Apply(this);
        Title = "TOR POS – Ersteinrichtung";
        Width = 760;
        Height = 470;
        MinWidth = 700;
        MinHeight = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var kiosk = EditionButton(
            "EINZELHANDEL",
            "Kiosk · Spätkauf · Supermarkt · Blumenladen · Friseur · Schneiderei · Shops",
            "#173A32",
            "#53E0C0");
        kiosk.Click += (_, _) => Close("KIOSK");

        var imbiss = EditionButton(
            "GASTRONOMIE",
            "Döner · Imbiss · Restaurant · Café · Bäckerei · Bar · Foodtruck",
            "#1A2D46",
            "#9CC3FF");
        imbiss.Click += (_, _) => Close("IMBISS");

        var choices = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 16,
            Children = { kiosk, imbiss }
        };
        Grid.SetColumn(imbiss, 1);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(30),
            Children =
            {
                new TextBlock
                {
                    Text = "ERSTEINRICHTUNG",
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = AppTheme.AccentTeal
                },
                AtRow(
                    new TextBlock
                    {
                        Text = "Welche Kassenart möchten Sie verwenden?",
                        FontSize = 25,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 8, 0, 20)
                    },
                    1),
                AtRow(choices, 2),
                AtRow(
                    new TextBlock
                    {
                        Text = "Die Auswahl wird für diese Installation gespeichert und kann später nicht versehentlich geändert werden.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.62,
                        Margin = new Thickness(0, 18, 0, 0)
                    },
                    3)
            }
        };
    }

    private static Button EditionButton(
        string title,
        string description,
        string background,
        string accent)
    {
        return new Button
        {
            MinHeight = 225,
            Background = new SolidColorBrush(Color.Parse(background)),
            BorderBrush = new SolidColorBrush(Color.Parse(accent)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 30,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse(accent)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = description,
                        FontSize = 15,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 270,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "AUSWÄHLEN",
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }

    private static T AtRow<T>(T control, int row)
        where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
