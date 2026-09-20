using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed record PaymentChoiceResult(PaymentMethod Method, bool ImHaus);

public sealed class PaymentChoiceWindow : Window
{
    private bool _imHaus;
    private readonly Button _outsideButton;
    private readonly Button _insideButton;

    public PaymentChoiceWindow(
        bool cashEnabled,
        bool cardEnabled,
        bool allowImHaus,
        bool defaultImHaus = false)
    {
        Title = "Zahlung";
        Width = 760;
        Height = allowImHaus ? 520 : 420;
        MinWidth = 640;
        MinHeight = allowImHaus ? 480 : 380;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        _imHaus = allowImHaus && defaultImHaus;

        _outsideButton = new Button
        {
            Content = "AUSSER HAUS\nSTANDARD",
            MinHeight = 74,
            FontSize = 18,
            FontWeight = FontWeight.Bold
        };
        _outsideButton.Click += (_, _) =>
        {
            _imHaus = false;
            RefreshServiceType();
        };

        _insideButton = new Button
        {
            Content = "IM HAUS",
            MinHeight = 74,
            FontSize = 18,
            FontWeight = FontWeight.Bold
        };
        _insideButton.Click += (_, _) =>
        {
            _imHaus = true;
            RefreshServiceType();
        };

        var serviceType = new StackPanel
        {
            IsVisible = allowImHaus,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "VERKAUFSART",
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#9DB4C9"))
                },
                new UniformGrid
                {
                    Columns = 2,
                    Rows = 1,
                    Children = { _outsideButton, _insideButton }
                },
                new TextBlock
                {
                    Text = "Standard ist AUSSER HAUS. IM HAUS gilt nur für diesen Verkauf.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#9DB4C9"))
                }
            }
        };

        var cash = new Button
        {
            Content = "BAR\nF1",
            MinHeight = 110,
            FontSize = 23,
            FontWeight = FontWeight.Bold,
            IsEnabled = cashEnabled,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder
        };
        cash.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Cash, _imHaus));

        var card = new Button
        {
            Content = "KARTE\nF2",
            MinHeight = 110,
            FontSize = 23,
            FontWeight = FontWeight.Bold,
            IsEnabled = cardEnabled,
            Background = AppTheme.InfoBlue,
            BorderBrush = AppTheme.InfoBlueBorder
        };
        card.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Card, _imHaus));

        var mixed = new Button
        {
            Content = "GEMISCHT\nBAR + KARTE",
            MinHeight = 110,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            IsEnabled = cashEnabled && cardEnabled,
            Background = new SolidColorBrush(Color.Parse("#5B3E8A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#A98AE0"))
        };
        mixed.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Mixed, _imHaus));

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 52,
            MinWidth = 180
        };
        cancel.Click += (_, _) => Close((PaymentChoiceResult?)null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "ZAHLUNG",
                    FontSize = 27,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                serviceType,
                new TextBlock
                {
                    Text = "ZAHLART WÄHLEN",
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#9DB4C9"))
                },
                new UniformGrid
                {
                    Columns = 3,
                    Rows = 1,
                    Children = { cash, card, mixed }
                },
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel }
                }
            }
        };

        RefreshServiceType();
        Opened += (_, _) => UiLanguage.Apply(this);
    }

    private void RefreshServiceType()
    {
        _outsideButton.Background = new SolidColorBrush(
            Color.Parse(_imHaus ? "#1B344C" : "#0F7A55"));
        _outsideButton.BorderBrush = new SolidColorBrush(
            Color.Parse(_imHaus ? "#496985" : "#53E0C0"));

        _insideButton.Background = new SolidColorBrush(
            Color.Parse(_imHaus ? "#8A5A1E" : "#1B344C"));
        _insideButton.BorderBrush = new SolidColorBrush(
            Color.Parse(_imHaus ? "#E6A94C" : "#496985"));
    }
}
