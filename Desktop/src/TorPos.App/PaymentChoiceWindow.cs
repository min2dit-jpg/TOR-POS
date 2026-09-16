using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed class PaymentChoiceWindow : Window
{
    public PaymentChoiceWindow(bool cashEnabled, bool cardEnabled)
    {
        Title = "Zahlart";
        Width = 620;
        Height = 330;
        MinWidth = 560;
        MinHeight = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var cash = new Button
        {
            Content = "BAR\nF1",
            MinHeight = 110,
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            IsEnabled = cashEnabled,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder
        };
        cash.Click += (_, _) => Close((PaymentMethod?)PaymentMethod.Cash);

        var card = new Button
        {
            Content = "KARTE\nF2",
            MinHeight = 110,
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            IsEnabled = cardEnabled,
            Background = AppTheme.InfoBlue,
            BorderBrush = AppTheme.InfoBlueBorder
        };
        card.Click += (_, _) => Close((PaymentMethod?)PaymentMethod.Card);

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 56,
            MinWidth = 180
        };
        cancel.Click += (_, _) => Close((PaymentMethod?)null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "ZAHLART WÄHLEN",
                    FontSize = 25,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                new TextBlock
                {
                    Text = "Unter Einstellungen → Zahlarten kann für F5 / KASSIEREN eine Standard-Zahlart festgelegt werden. Dann entfällt diese Auswahl.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#9DB4C9"))
                },
                new UniformGrid
                {
                    Columns = 2,
                    Rows = 1,
                    Children = { cash, card }
                },
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel }
                }
            }
        };

        Opened += (_, _) => UiLanguage.Apply(this);
    }
}
