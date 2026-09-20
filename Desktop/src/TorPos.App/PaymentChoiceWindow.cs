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
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9DB4C9"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#111F30"));
    private static readonly IBrush PanelBorder = new SolidColorBrush(Color.Parse("#294765"));

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
        Width = 720;
        Height = allowImHaus ? 500 : 390;
        MinWidth = 620;
        MinHeight = allowImHaus ? 460 : 350;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        _imHaus = allowImHaus && defaultImHaus;

        _outsideButton = ChoiceButton("AUSSER HAUS\nSTANDARD");
        _outsideButton.Click += (_, _) =>
        {
            _imHaus = false;
            RefreshServiceType();
        };

        _insideButton = ChoiceButton("IM HAUS");
        _insideButton.Click += (_, _) =>
        {
            _imHaus = true;
            RefreshServiceType();
        };

        var serviceButtons = new UniformGrid
        {
            Columns = 2,
            Rows = 1,
            Children = { _outsideButton, _insideButton }
        };

        var serviceType = new Border
        {
            IsVisible = allowImHaus,
            Background = Panel,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    SectionTitle("VERKAUFSART"),
                    serviceButtons,
                    new TextBlock
                    {
                        Text = "Standard: AUSSER HAUS · IM HAUS gilt nur für diesen Verkauf.",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Muted
                    }
                }
            }
        };

        var cash = PaymentButton(
            "BAR\nF1",
            cashEnabled,
            AppTheme.SuccessGreen,
            AppTheme.SuccessGreenBorder,
            22);
        cash.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Cash, _imHaus));

        var card = PaymentButton(
            "KARTE\nF2",
            cardEnabled,
            AppTheme.InfoBlue,
            AppTheme.InfoBlueBorder,
            22);
        card.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Card, _imHaus));

        var mixed = PaymentButton(
            "GEMISCHT\nBAR + KARTE",
            cashEnabled && cardEnabled,
            new SolidColorBrush(Color.Parse("#5B3E8A")),
            new SolidColorBrush(Color.Parse("#A98AE0")),
            18);
        mixed.Click += (_, _) => Close(new PaymentChoiceResult(PaymentMethod.Mixed, _imHaus));

        var paymentButtons = new UniformGrid
        {
            Columns = 3,
            Rows = 1,
            Children = { cash, card, mixed }
        };

        var paymentType = new Border
        {
            Background = Panel,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    SectionTitle("ZAHLART"),
                    paymentButtons
                }
            }
        };

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#2B3645")),
            BorderBrush = new SolidColorBrush(Color.Parse("#53657A")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8)
        };
        cancel.Click += (_, _) => Close((PaymentChoiceResult?)null);

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "ZAHLUNG",
                                FontSize = 26,
                                FontWeight = FontWeight.Bold,
                                Foreground = Brushes.White
                            },
                            new TextBlock
                            {
                                Text = "Verkaufsart und Zahlart auswählen",
                                FontSize = 12,
                                Foreground = Muted
                            }
                        }
                    },
                    serviceType,
                    paymentType,
                    cancel
                }
            }
        };

        RefreshServiceType();
        Opened += (_, _) => UiLanguage.Apply(this);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Bold,
        Foreground = Muted
    };

    private static Button ChoiceButton(string text) => new()
    {
        Content = text,
        MinHeight = 68,
        Margin = new Thickness(4, 0),
        Padding = new Thickness(12, 8),
        FontSize = 17,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(8)
    };

    private static Button PaymentButton(
        string text,
        bool enabled,
        IBrush background,
        IBrush border,
        double fontSize) => new()
    {
        Content = text,
        MinHeight = 105,
        Margin = new Thickness(4, 0),
        Padding = new Thickness(10),
        FontSize = fontSize,
        FontWeight = FontWeight.Bold,
        IsEnabled = enabled,
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(9)
    };

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
