using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed record PaymentChoiceResult(
    PaymentMethod Method,
    bool ImHaus,
    CashPaymentResult? Cash = null,
    long CashPortionCents = 0,
    bool UiConfirmed = false);

/// <summary>
/// R166: one payment surface owns service type, tender selection and the
/// tender-specific inputs. Choosing BAR/KARTE/GEMISCHT no longer opens a
/// second application window.
/// </summary>
public sealed class PaymentChoiceWindow : Window
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9DB4C9"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#111F30"));
    private static readonly IBrush PanelBorder = new SolidColorBrush(Color.Parse("#294765"));

    private readonly long _totalCents;
    private readonly bool _cashEnabled;
    private readonly bool _cardEnabled;
    private readonly bool _allowImHaus;
    private readonly string _cashLabel;
    private readonly string _cardLabel;
    private bool _imHaus;
    private PaymentMethod _method;

    private readonly Button _outsideButton;
    private readonly Button _insideButton;
    private readonly Button _cashButton;
    private readonly Button _cardButton;
    private readonly Button _mixedButton;
    private readonly Border _detailHost;
    private readonly Button _finishButton;

    private readonly TextBox _cashGiven = new()
    {
        FontSize = 24,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        PlaceholderText = "0,00",
        MinHeight = 52
    };

    private readonly TextBlock _change = new()
    {
        FontSize = 28,
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
        FontSize = 28,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = Brushes.LightSkyBlue
    };

    public PaymentChoiceWindow(
        long totalCents,
        bool cashEnabled,
        bool cardEnabled,
        bool allowImHaus,
        bool defaultImHaus = false,
        PaymentMethod? preferredMethod = null,
        bool simulation = false,
        bool training = false,
        string cashLabel = "BAR",
        string cardLabel = "KARTE")
    {
        _totalCents = totalCents;
        _cashEnabled = cashEnabled;
        _cardEnabled = cardEnabled;
        _allowImHaus = allowImHaus;
        _cashLabel = string.IsNullOrWhiteSpace(cashLabel) ? "BAR" : cashLabel.Trim().ToUpperInvariant();
        _cardLabel = string.IsNullOrWhiteSpace(cardLabel) ? "KARTE" : cardLabel.Trim().ToUpperInvariant();
        _imHaus = allowImHaus && defaultImHaus;
        _method = ResolveInitialMethod(preferredMethod);

        Title = "Zahlung";
        Width = 820;
        Height = allowImHaus ? 720 : 610;
        MinWidth = 700;
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

        _cashButton = PaymentButton(_cashLabel, _cashEnabled, AppTheme.SuccessGreen, AppTheme.SuccessGreenBorder);
        _cashButton.Click += (_, _) => SelectMethod(PaymentMethod.Cash);

        _cardButton = PaymentButton(_cardLabel, _cardEnabled && totalCents >= 0, AppTheme.InfoBlue, AppTheme.InfoBlueBorder);
        _cardButton.Click += (_, _) => SelectMethod(PaymentMethod.Card);

        _mixedButton = PaymentButton(
            "GEMISCHT",
            _cashEnabled && _cardEnabled && totalCents > 0,
            new SolidColorBrush(Color.Parse("#5B3E8A")),
            new SolidColorBrush(Color.Parse("#A98AE0")));
        _mixedButton.Click += (_, _) => SelectMethod(PaymentMethod.Mixed);

        _detailHost = new Border
        {
            Background = Panel,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            MinHeight = 205
        };

        _finishButton = new Button
        {
            Content = "ZAHLUNG ABSCHLIESSEN",
            MinHeight = 62,
            FontSize = 19,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder,
            CornerRadius = new CornerRadius(9)
        };
        _finishButton.Click += (_, _) => Finish();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 62,
            MinWidth = 180,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Background = new SolidColorBrush(Color.Parse("#2B3645")),
            BorderBrush = new SolidColorBrush(Color.Parse("#53657A")),
            Foreground = Brushes.White,
            CornerRadius = new CornerRadius(9)
        };
        cancel.Click += (_, _) => Close((PaymentChoiceResult?)null);

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

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { cancel, _finishButton }
        };
        Grid.SetColumn(_finishButton, 1);

        var mode = training
            ? "TRAINING · KEINE ECHTE BUCHUNG"
            : simulation
                ? "TEST · KEINE ECHTE ZAHLUNG"
                : "ZAHLUNG";

        Content = new ScrollViewer
        {
            Margin = new Thickness(22),
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
                                Text = mode,
                                FontSize = 12,
                                FontWeight = FontWeight.Bold,
                                Foreground = simulation || training ? AppTheme.WarningAmber : AppTheme.AccentTeal
                            },
                            new Grid
                            {
                                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "KASSIEREN",
                                        FontSize = 28,
                                        FontWeight = FontWeight.Bold,
                                        Foreground = Brushes.White,
                                        VerticalAlignment = VerticalAlignment.Center
                                    },
                                    BuildTotalBlock(totalCents)
                                }
                            }
                        }
                    },
                    serviceType,
                    paymentType,
                    _detailHost,
                    footer
                }
            }
        };
        if (Content is ScrollViewer scroll &&
            scroll.Content is StackPanel root &&
            root.Children[0] is StackPanel header &&
            header.Children[1] is Grid totalGrid)
        {
            Grid.SetColumn(totalGrid.Children[1], 1);
        }

        _cashGiven.TextChanged += (_, _) => RefreshCashState();
        _mixedCash.TextChanged += (_, _) => RefreshMixedState();
        KeyDown += OnWindowKeyDown;

        if (totalCents > 0)
            _cashGiven.Text = MoneyInput(totalCents);

        if (totalCents > 1)
            _mixedCash.Text = MoneyInput(Math.Max(1, totalCents / 2));

        RefreshServiceType();
        SelectMethod(_method);
        Opened += (_, _) => UiLanguage.Apply(this);
    }

    private PaymentMethod ResolveInitialMethod(PaymentMethod? preferred)
    {
        if (preferred == PaymentMethod.Cash && _cashEnabled)
            return PaymentMethod.Cash;
        if (preferred == PaymentMethod.Card && _cardEnabled && _totalCents >= 0)
            return PaymentMethod.Card;
        if (preferred == PaymentMethod.Mixed && _cashEnabled && _cardEnabled && _totalCents > 0)
            return PaymentMethod.Mixed;
        if (_cashEnabled)
            return PaymentMethod.Cash;
        return PaymentMethod.Card;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Bold,
        Foreground = Muted
    };

    private static Control BuildTotalBlock(long totalCents) => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        Children =
        {
            new TextBlock
            {
                Text = "ZU ZAHLEN",
                FontSize = 10,
                Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Right
            },
            new TextBlock
            {
                Text = Formatting.Money(totalCents),
                FontSize = 30,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right
            }
        }
    };

    private static Button ChoiceButton(string text) => new()
    {
        Content = text,
        MinHeight = 62,
        Margin = new Thickness(4, 0),
        Padding = new Thickness(12, 8),
        FontSize = 16,
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
        IBrush border) => new()
    {
        Content = text,
        MinHeight = 74,
        Margin = new Thickness(4, 0),
        Padding = new Thickness(10),
        FontSize = 17,
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

    private void SelectMethod(PaymentMethod method)
    {
        if (method == PaymentMethod.Cash && !_cashEnabled)
            return;
        if (method == PaymentMethod.Card && (!_cardEnabled || _totalCents < 0))
            return;
        if (method == PaymentMethod.Mixed && (!_cashEnabled || !_cardEnabled || _totalCents <= 0))
            return;

        _method = method;
        RefreshMethodButtons();

        _detailHost.Child = method switch
        {
            PaymentMethod.Cash => BuildCashPanel(),
            PaymentMethod.Card => BuildCardPanel(),
            PaymentMethod.Mixed => BuildMixedPanel(),
            _ => BuildCardPanel()
        };

        RefreshFinishState();
    }

    private Control BuildCashPanel()
    {
        if (_totalCents <= 0)
        {
            return new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    SectionTitle(_totalCents < 0 ? "BARAUSZAHLUNG" : "BAR"),
                    new TextBlock
                    {
                        Text = _totalCents < 0
                            ? $"An den Kunden auszahlen: {Formatting.Money(-_totalCents)}"
                            : "Kein Zahlbetrag offen.",
                        FontSize = 23,
                        FontWeight = FontWeight.Bold,
                        Foreground = _totalCents < 0 ? AppTheme.WarningAmber : Brushes.White
                    },
                    new TextBlock
                    {
                        Text = _totalCents < 0
                            ? "Mit ZAHLUNG ABSCHLIESSEN bestätigen, sobald der Betrag ausgezahlt wurde."
                            : "Mit ZAHLUNG ABSCHLIESSEN fortfahren.",
                        Foreground = Muted,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };
        }

        var exact = TenderButton($"PASSEND\n{Formatting.Money(_totalCents)}", _totalCents);
        var quick = new UniformGrid { Columns = 3, Rows = 1 };
        foreach (var amount in BuildQuickAmounts(_totalCents))
            quick.Children.Add(TenderButton(Formatting.Money(amount), amount));

        return new StackPanel
        {
            Spacing = 9,
            Children =
            {
                SectionTitle("BARZAHLUNG · GEGEBEN"),
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("1.15*,2.85*"),
                    ColumnSpacing = 8,
                    Children = { exact, quick }
                }.WithColumn(quick, 1),
                _cashGiven,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "RÜCKGELD",
                            FontWeight = FontWeight.Bold,
                            Foreground = Muted,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        _change
                    }
                }.WithColumn(_change, 1)
            }
        };
    }

    private Control BuildCardPanel() => new StackPanel
    {
        Spacing = 10,
        Children =
        {
            SectionTitle("KARTENZAHLUNG"),
            new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0E2033")),
                BorderBrush = AppTheme.InfoBlueBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18),
                Child = new StackPanel
                {
                    Spacing = 5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Formatting.Money(_totalCents),
                            FontSize = 34,
                            FontWeight = FontWeight.Bold,
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = "Mit ZAHLUNG ABSCHLIESSEN wird die Kartenzahlung gestartet.",
                            Foreground = Muted,
                            TextAlignment = TextAlignment.Center,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            }
        }
    };

    private Control BuildMixedPanel()
    {
        var percentages = new UniformGrid { Columns = 3, Rows = 1 };
        foreach (var fraction in new[] { 0.25m, 0.5m, 0.75m })
        {
            var amount = (long)Math.Round(_totalCents * fraction, MidpointRounding.AwayFromZero);
            if (amount <= 0 || amount >= _totalCents)
                continue;
            percentages.Children.Add(TenderButton($"{fraction * 100:0} % BAR\n{Formatting.Money(amount)}", amount, _mixedCash));
        }

        return new StackPanel
        {
            Spacing = 9,
            Children =
            {
                SectionTitle("GEMISCHT · BAR + KARTE"),
                percentages,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    ColumnSpacing = 10,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = "BAR-ANTEIL", Foreground = Muted, FontWeight = FontWeight.Bold },
                                _mixedCash
                            }
                        },
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = "KARTEN-ANTEIL", Foreground = Muted, FontWeight = FontWeight.Bold },
                                _mixedCard
                            }
                        }
                    }
                }.SetSecondColumn(),
                new TextBlock
                {
                    Text = "Der eingegebene Bar-Anteil wird kassiert; der Rest wird als Kartenzahlung verarbeitet.",
                    Foreground = Muted,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private Button TenderButton(string text, long cents, TextBox? target = null)
    {
        var button = new Button
        {
            Content = text,
            Tag = cents,
            MinHeight = 58,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse("#263E54")),
            BorderBrush = new SolidColorBrush(Color.Parse("#5C7690")),
            CornerRadius = new CornerRadius(8)
        };
        button.Click += (_, _) => (target ?? _cashGiven).Text = MoneyInput(cents);
        return button;
    }

    private static IReadOnlyList<long> BuildQuickAmounts(long totalCents)
    {
        static long RoundUp(long cents, long step) => ((cents + step - 1) / step) * step;
        var candidates = new[]
        {
            RoundUp(totalCents, 500),
            RoundUp(totalCents, 1000),
            RoundUp(totalCents, 2000),
            RoundUp(totalCents, 5000),
            RoundUp(totalCents, 10000)
        };

        var result = candidates
            .Where(x => x >= totalCents && x != totalCents)
            .Distinct()
            .Take(3)
            .ToList();

        while (result.Count < 3)
        {
            var next = result.Count == 0
                ? RoundUp(totalCents + 1, 1000)
                : result[^1] + (result[^1] < 5000 ? 1000 : 5000);
            if (!result.Contains(next))
                result.Add(next);
        }

        return result;
    }

    private static string MoneyInput(long cents) =>
        Formatting.Money(cents).Replace(" €", "", StringComparison.Ordinal);

    private void RefreshServiceType()
    {
        _outsideButton.Background = new SolidColorBrush(Color.Parse(_imHaus ? "#1B344C" : "#0F7A55"));
        _outsideButton.BorderBrush = new SolidColorBrush(Color.Parse(_imHaus ? "#496985" : "#53E0C0"));
        _insideButton.Background = new SolidColorBrush(Color.Parse(_imHaus ? "#8A5A1E" : "#1B344C"));
        _insideButton.BorderBrush = new SolidColorBrush(Color.Parse(_imHaus ? "#E6A94C" : "#496985"));
    }

    private void RefreshMethodButtons()
    {
        ApplyPaymentSelection(_cashButton, _method == PaymentMethod.Cash, "#0E9F47", "#4DDA82");
        ApplyPaymentSelection(_cardButton, _method == PaymentMethod.Card, "#1766D1", "#65A0FF");
        ApplyPaymentSelection(_mixedButton, _method == PaymentMethod.Mixed, "#5B3E8A", "#A98AE0");
    }

    private static void ApplyPaymentSelection(Button button, bool selected, string activeBackground, string activeBorder)
    {
        button.Background = new SolidColorBrush(Color.Parse(selected ? activeBackground : "#1B344C"));
        button.BorderBrush = new SolidColorBrush(Color.Parse(selected ? activeBorder : "#496985"));
        button.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    private void RefreshCashState()
    {
        var valid = Formatting.TryParseMoney(_cashGiven.Text, out var tendered) && tendered >= _totalCents;
        _change.Text = valid
            ? Formatting.Money(tendered - _totalCents)
            : "0,00 €";
        _change.Foreground = valid ? Brushes.LightGreen : Brushes.Orange;
        RefreshFinishState();
    }

    private void RefreshMixedState()
    {
        var valid = Formatting.TryParseMoney(_mixedCash.Text, out var cash) && cash > 0 && cash < _totalCents;
        _mixedCard.Text = valid
            ? Formatting.Money(_totalCents - cash)
            : "0,00 €";
        _mixedCard.Foreground = valid ? Brushes.LightSkyBlue : Brushes.Orange;
        RefreshFinishState();
    }

    private void RefreshFinishState()
    {
        _finishButton.Background = _method switch
        {
            PaymentMethod.Card => AppTheme.InfoBlue,
            PaymentMethod.Mixed => new SolidColorBrush(Color.Parse("#5B3E8A")),
            _ => AppTheme.SuccessGreen
        };
        _finishButton.BorderBrush = _method switch
        {
            PaymentMethod.Card => AppTheme.InfoBlueBorder,
            PaymentMethod.Mixed => new SolidColorBrush(Color.Parse("#A98AE0")),
            _ => AppTheme.SuccessGreenBorder
        };

        _finishButton.IsEnabled = _method switch
        {
            PaymentMethod.Cash when _totalCents <= 0 => _cashEnabled,
            PaymentMethod.Cash => _cashEnabled &&
                                  Formatting.TryParseMoney(_cashGiven.Text, out var tendered) &&
                                  tendered >= _totalCents,
            PaymentMethod.Card => _cardEnabled && _totalCents >= 0,
            PaymentMethod.Mixed => _cashEnabled && _cardEnabled &&
                                   Formatting.TryParseMoney(_mixedCash.Text, out var cash) &&
                                   cash > 0 && cash < _totalCents,
            _ => false
        };

        _finishButton.Content = _method switch
        {
            PaymentMethod.Card => "KARTENZAHLUNG STARTEN",
            PaymentMethod.Mixed => "GEMISCHT ABSCHLIESSEN",
            PaymentMethod.Cash when _totalCents < 0 => "AUSZAHLUNG BESTÄTIGEN",
            _ => "BARZAHLUNG ABSCHLIESSEN"
        };
    }

    private void Finish()
    {
        if (!_finishButton.IsEnabled)
            return;

        CashPaymentResult? cashResult = null;
        long mixedCashCents = 0;

        if (_method == PaymentMethod.Cash)
        {
            if (_totalCents > 0)
            {
                if (!Formatting.TryParseMoney(_cashGiven.Text, out var tendered) || tendered < _totalCents)
                    return;
                cashResult = new CashPaymentResult(tendered, tendered - _totalCents);
            }
            else
            {
                cashResult = new CashPaymentResult(0, 0);
            }
        }
        else if (_method == PaymentMethod.Mixed)
        {
            if (!Formatting.TryParseMoney(_mixedCash.Text, out mixedCashCents) ||
                mixedCashCents <= 0 ||
                mixedCashCents >= _totalCents)
                return;
        }

        Close(new PaymentChoiceResult(
            _method,
            _allowImHaus && _imHaus,
            cashResult,
            mixedCashCents,
            UiConfirmed: true));
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close((PaymentChoiceResult?)null);
            return;
        }

        if (e.Key == Key.F1 && _cashEnabled)
        {
            e.Handled = true;
            SelectMethod(PaymentMethod.Cash);
        }
        else if (e.Key == Key.F2 && _cardEnabled && _totalCents >= 0)
        {
            e.Handled = true;
            SelectMethod(PaymentMethod.Card);
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            Finish();
        }
    }
}

internal static class PaymentChoiceLayoutExtensions
{
    internal static Grid WithColumn(this Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        return grid;
    }

    internal static Grid SetSecondColumn(this Grid grid)
    {
        if (grid.Children.Count > 1)
            Grid.SetColumn(grid.Children[1], 1);
        return grid;
    }
}
