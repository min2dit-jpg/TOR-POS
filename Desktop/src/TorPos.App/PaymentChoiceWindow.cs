using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

// R168: one payment page owns tender choice and the amount details. The user
// never has to click BAR/KARTE on the cashier and then choose BAR/KARTE again.
public sealed record PaymentChoiceResult(
    PaymentMethod Method,
    bool ImHaus,
    long CashTenderedCents = 0,
    long CashPortionCents = 0);

public sealed class PaymentChoiceWindow : Window
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9DB4C9"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#111F30"));
    private static readonly IBrush PanelBorder = new SolidColorBrush(Color.Parse("#294765"));

    private readonly long _totalCents;
    private readonly bool _simulation;
    private bool _imHaus;
    private PaymentMethod? _selectedMethod;

    private readonly Button _outsideButton;
    private readonly Button _insideButton;
    private readonly Button _cashButton;
    private readonly Button _cardButton;
    private readonly Button _mixedButton;
    private readonly Button _accept;
    private readonly Border _cashDetail;
    private readonly Border _cardDetail;
    private readonly Border _mixedDetail;

    private readonly TextBox _cashGiven = new()
    {
        FontSize = 24,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        PlaceholderText = "0,00",
        MinHeight = 52
    };

    private readonly TextBlock _cashChange = new()
    {
        FontSize = 30,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = Brushes.LightGreen
    };

    private readonly TextBox _mixedCash = new()
    {
        FontSize = 24,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        PlaceholderText = "0,00",
        MinHeight = 52
    };

    private readonly TextBlock _mixedCard = new()
    {
        FontSize = 30,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = Brushes.LightSkyBlue
    };

    private readonly TextBlock _validation = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#FFB74D")),
        MinHeight = 18
    };

    public PaymentChoiceWindow(
        bool cashEnabled,
        bool cardEnabled,
        bool allowImHaus,
        bool defaultImHaus = false,
        long totalCents = 1234,
        bool simulation = true)
    {
        _totalCents = totalCents;
        _simulation = simulation;
        _imHaus = allowImHaus && defaultImHaus;

        Title = "Zahlung";
        Width = 780;
        Height = allowImHaus ? 720 : 610;
        MinWidth = 680;
        MinHeight = allowImHaus ? 640 : 540;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

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

        _cashButton = PaymentButton(
            "BAR\nF1",
            cashEnabled,
            AppTheme.SuccessGreen,
            AppTheme.SuccessGreenBorder,
            22);
        _cashButton.Click += (_, _) => SelectMethod(PaymentMethod.Cash);

        _cardButton = PaymentButton(
            "KARTE\nF2",
            cardEnabled && totalCents > 0,
            AppTheme.InfoBlue,
            AppTheme.InfoBlueBorder,
            22);
        _cardButton.Click += (_, _) => SelectMethod(PaymentMethod.Card);

        _mixedButton = PaymentButton(
            "GEMISCHT\nBAR + KARTE",
            cashEnabled && cardEnabled && totalCents > 0,
            new SolidColorBrush(Color.Parse("#5B3E8A")),
            new SolidColorBrush(Color.Parse("#A98AE0")),
            18);
        _mixedButton.Click += (_, _) => SelectMethod(PaymentMethod.Mixed);

        var paymentButtons = new UniformGrid
        {
            Columns = 3,
            Rows = 1,
            Children = { _cashButton, _cardButton, _mixedButton }
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

        _cashGiven.Text = totalCents > 0
            ? Formatting.Money(totalCents).Replace(" €", "")
            : "0,00";
        _cashGiven.TextChanged += (_, _) => RefreshAcceptState();

        var cashQuick = new UniformGrid { Columns = 4, Rows = 1 };
        foreach (var amount in CashQuickAmounts(totalCents))
        {
            var button = new Button
            {
                Content = amount == totalCents
                    ? $"PASSEND\n{Formatting.Money(amount)}"
                    : Formatting.Money(amount),
                MinHeight = 58,
                Margin = new Thickness(3),
                FontWeight = FontWeight.Bold,
                Tag = amount
            };
            button.Click += (_, _) =>
                _cashGiven.Text = Formatting.Money((long)button.Tag!).Replace(" €", "");
            cashQuick.Children.Add(button);
        }

        _cashDetail = DetailPanel(
            new TextBlock
            {
                Text = totalCents < 0
                    ? $"AUSZAHLUNG: {Formatting.Money(-totalCents)}"
                    : $"ZU ZAHLEN: {Formatting.Money(totalCents)}",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            },
            totalCents > 0
                ? new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "GEGEBEN", FontWeight = FontWeight.Bold, Foreground = Muted },
                        cashQuick,
                        _cashGiven,
                        BuildAmountRow("RÜCKGELD", _cashChange)
                    }
                }
                : new TextBlock
                {
                    Text = "Pfand-/Barauszahlung wird nach KASSIEREN nochmals sicher bestätigt.",
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap
                });

        _cardDetail = DetailPanel(
            new TextBlock
            {
                Text = $"KARTENZAHLUNG · {Formatting.Money(Math.Max(0, totalCents))}",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            },
            new TextBlock
            {
                Text = simulation
                    ? "TEST: Mit KASSIEREN wird die Kartenzahlung in dieser Testkasse bestätigt. Es wird keine echte Karte belastet."
                    : "Mit KASSIEREN wird die Zahlung an das konfigurierte Kartenterminal übergeben. Keine zweite Zahlart-Seite.",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted
            });

        _mixedCash.TextChanged += (_, _) => RefreshAcceptState();
        var mixedQuick = new UniformGrid { Columns = 3, Rows = 1 };
        foreach (var fraction in new[] { 0.25m, 0.5m, 0.75m })
        {
            var amount = (long)Math.Round(totalCents * fraction, MidpointRounding.AwayFromZero);
            if (amount <= 0 || amount >= totalCents)
                continue;

            var button = new Button
            {
                Content = $"{fraction * 100:0} % BAR\n{Formatting.Money(amount)}",
                MinHeight = 58,
                Margin = new Thickness(3),
                FontWeight = FontWeight.Bold,
                Tag = amount
            };
            button.Click += (_, _) =>
                _mixedCash.Text = Formatting.Money((long)button.Tag!).Replace(" €", "");
            mixedQuick.Children.Add(button);
        }

        _mixedDetail = DetailPanel(
            new TextBlock
            {
                Text = $"GESAMT: {Formatting.Money(Math.Max(0, totalCents))}",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White
            },
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    mixedQuick,
                    new TextBlock { Text = "BAR-ANTEIL", FontWeight = FontWeight.Bold, Foreground = Muted },
                    _mixedCash,
                    BuildAmountRow("KARTEN-ANTEIL", _mixedCard)
                }
            });

        var detailHost = new Grid
        {
            Children = { _cashDetail, _cardDetail, _mixedDetail }
        };

        _accept = new Button
        {
            Content = "KASSIEREN",
            MinHeight = 58,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder,
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8)
        };
        _accept.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 58,
            MinWidth = 180,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#2B3645")),
            BorderBrush = new SolidColorBrush(Color.Parse("#53657A")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(8)
        };
        cancel.Click += (_, _) => Close((PaymentChoiceResult?)null);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { cancel, _accept }
        };
        Grid.SetColumn(_accept, 1);

        var paymentBody = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
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
                                Text = "Eine Seite: Verkaufsart, Zahlart und Betrag",
                                FontSize = 12,
                                Foreground = Muted
                            }
                        }
                    },
                    serviceType,
                    paymentType,
                    detailHost,
                    _validation
                }
            }
        };

        var paymentLayout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 12,
            Children = { paymentBody, footer }
        };
        Grid.SetRow(footer, 1);

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = paymentLayout
        };

        RefreshServiceType();
        SelectMethod(cashEnabled
            ? PaymentMethod.Cash
            : cardEnabled && totalCents > 0
                ? PaymentMethod.Card
                : PaymentMethod.Cash);

        Opened += (_, _) => UiLanguage.Apply(this);
    }

    private void SelectMethod(PaymentMethod method)
    {
        _selectedMethod = method;
        _cashDetail.IsVisible = method == PaymentMethod.Cash;
        _cardDetail.IsVisible = method == PaymentMethod.Card;
        _mixedDetail.IsVisible = method == PaymentMethod.Mixed;

        _cashButton.Opacity = method == PaymentMethod.Cash ? 1 : 0.62;
        _cardButton.Opacity = method == PaymentMethod.Card ? 1 : 0.62;
        _mixedButton.Opacity = method == PaymentMethod.Mixed ? 1 : 0.62;

        _accept.Content = method switch
        {
            PaymentMethod.Cash => "BARZAHLUNG · KASSIEREN",
            PaymentMethod.Card => _simulation ? "KARTE · TEST BESTÄTIGEN" : "KARTENZAHLUNG STARTEN",
            PaymentMethod.Mixed => "GEMISCHT · KASSIEREN",
            _ => "KASSIEREN"
        };

        RefreshAcceptState();
    }

    private void RefreshAcceptState()
    {
        _validation.Text = "";

        if (_selectedMethod == PaymentMethod.Cash)
        {
            if (_totalCents <= 0)
            {
                _cashChange.Text = "0,00 €";
                _accept.IsEnabled = _cashButton.IsEnabled;
                return;
            }

            if (!Formatting.TryParseMoney(_cashGiven.Text, out var given) || given < _totalCents)
            {
                _cashChange.Text = "0,00 €";
                _validation.Text = "Gegebener Betrag muss mindestens dem Zahlbetrag entsprechen.";
                _accept.IsEnabled = false;
                return;
            }

            _cashChange.Text = Formatting.Money(given - _totalCents);
            _accept.IsEnabled = _cashButton.IsEnabled;
            return;
        }

        if (_selectedMethod == PaymentMethod.Card)
        {
            _accept.IsEnabled = _cardButton.IsEnabled;
            return;
        }

        if (_selectedMethod == PaymentMethod.Mixed)
        {
            if (!Formatting.TryParseMoney(_mixedCash.Text, out var cash) ||
                cash <= 0 ||
                cash >= _totalCents)
            {
                _mixedCard.Text = "0,00 €";
                _validation.Text = "BAR-Anteil muss größer 0 und kleiner als der Gesamtbetrag sein.";
                _accept.IsEnabled = false;
                return;
            }

            _mixedCard.Text = Formatting.Money(_totalCents - cash);
            _accept.IsEnabled = _mixedButton.IsEnabled;
            return;
        }

        _accept.IsEnabled = false;
    }

    private void Accept()
    {
        if (_selectedMethod is null || !_accept.IsEnabled)
            return;

        if (_selectedMethod == PaymentMethod.Cash)
        {
            long tendered = 0;
            if (_totalCents > 0 &&
                !Formatting.TryParseMoney(_cashGiven.Text, out tendered))
                return;

            Close(new PaymentChoiceResult(
                PaymentMethod.Cash,
                _imHaus,
                CashTenderedCents: tendered));
            return;
        }

        if (_selectedMethod == PaymentMethod.Mixed)
        {
            if (!Formatting.TryParseMoney(_mixedCash.Text, out var cash) ||
                cash <= 0 ||
                cash >= _totalCents)
                return;

            Close(new PaymentChoiceResult(
                PaymentMethod.Mixed,
                _imHaus,
                CashPortionCents: cash));
            return;
        }

        Close(new PaymentChoiceResult(PaymentMethod.Card, _imHaus));
    }

    private static Border DetailPanel(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var control in controls)
            panel.Children.Add(control);

        return new Border
        {
            Background = Panel,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Child = panel
        };
    }

    private static Control BuildAmountRow(string label, TextBlock value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeight.Bold,
                    Foreground = Muted,
                    VerticalAlignment = VerticalAlignment.Center
                },
                value
            }
        };
        Grid.SetColumn(value, 1);
        return grid;
    }

    private static IEnumerable<long> CashQuickAmounts(long totalCents)
    {
        if (totalCents <= 0)
            return Array.Empty<long>();

        return new[] { totalCents, 1000L, 2000L, 5000L, 10000L, 20000L }
            .Where(x => x >= totalCents)
            .Distinct()
            .Take(4);
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
        MinHeight = 92,
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
