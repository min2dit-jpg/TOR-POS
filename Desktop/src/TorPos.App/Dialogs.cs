using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class VariantWindow:Window
{
    public VariantWindow(Product p)
    {
        Title="Größe wählen";Width=520;Height=500;CanResize=false;
        var panel=new StackPanel{Margin=new Avalonia.Thickness(24),Spacing=10};
        panel.Children.Add(new TextBlock{Text=p.Name,FontSize=24,FontWeight=FontWeight.Bold});
        panel.Children.Add(new TextBlock{Text="Größe / Variante auswählen",Opacity=.65});

        foreach(var v in p.Variants)
        {
            var b=new Button
            {
                Content=$"{v.Name}     {Formatting.Money(v.PriceCents+p.PfandCents)}",
                MinHeight=64,Tag=v
            };
            b.Click+=(_,_)=>Close((ProductVariant?)v);
            panel.Children.Add(b);
        }

        var cancel=new Button{Content="ABBRECHEN",MinHeight=48};
        cancel.Click+=(_,_)=>Close((ProductVariant?)null);
        panel.Children.Add(cancel);Content=panel;
        Opened += (_,_) => UiLanguage.Apply(this);
    }
}

public sealed class MoneyInputWindow:Window
{
    private readonly TextBox _input=new();

    public MoneyInputWindow(string title,string prompt)
    {
        Title=title;Width=420;Height=220;CanResize=false;
        _input.FontSize=22;_input.KeyDown+=OnKey;
        var ok=new Button{Content="OK",MinHeight=48};
        ok.Click+=(_,_)=>Accept();
        Content=new StackPanel
        {
            Margin=new Avalonia.Thickness(24),Spacing=12,
            Children={new TextBlock{Text=prompt,FontSize=18},_input,ok}
        };
        Opened+=(_,_)=>_input.Focus();
    }

    private void OnKey(object? s,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;Accept();}}
    private void Accept(){if(Formatting.TryParseMoney(_input.Text,out var c))Close((long?)c);}
}

public sealed record CashPaymentResult(
    long TenderedCents,
    long ChangeCents);

public sealed class CashPaymentWindow : Window
{
    private readonly long _totalCents;
    private readonly TextBox _given = new()
    {
        FontSize = 26,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        PlaceholderText = "0,00",
        MinHeight = 56
    };

    private readonly TextBlock _change = new()
    {
        FontSize = 34,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = Brushes.LightGreen
    };

    private readonly Button _accept = new()
    {
        MinHeight = 72,
        FontSize = 22,
        FontWeight = FontWeight.Bold,
        Content = "KASSIEREN",
        IsEnabled = false,
        Background = AppTheme.SuccessGreen,
        BorderBrush = AppTheme.SuccessGreenBorder
    };

    public CashPaymentWindow(long totalCents)
    {
        _totalCents = totalCents;
        Title = "Barzahlung";
        Width = 720;
        Height = 610;
        MinWidth = 660;
        MinHeight = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _given.TextChanged += (_, _) => RefreshChange();
        _given.KeyDown += OnInputKeyDown;
        _accept.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 72,
            FontSize = 18,
            MinWidth = 180
        };
        cancel.Click += (_, _) => Close((CashPaymentResult?)null);

        var exact = CreateTenderButton(
            $"PASSEND\n{Formatting.Money(totalCents)}",
            totalCents,
            "#1766D1",
            "#65A0FF");

        var quickGrid = new UniformGrid
        {
            Columns = 3,
            Rows = 1
        };

        foreach (var amount in BuildQuickAmounts(totalCents))
            quickGrid.Children.Add(CreateTenderButton(Formatting.Money(amount), amount));

        var keypad = new UniformGrid
        {
            Columns = 3,
            Rows = 4,
            IsVisible = false
        };

        foreach (var label in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "C", "0", "," })
        {
            var button = new Button
            {
                Content = label,
                MinHeight = 52,
                FontSize = 21,
                FontWeight = FontWeight.Bold,
                Tag = label
            };
            button.Click += (_, _) => HandleKeypad(label);
            keypad.Children.Add(button);
        }

        var customAmount = new Button
        {
            Content = "ANDERER BETRAG",
            MinHeight = 48,
            FontWeight = FontWeight.Bold
        };
        customAmount.Click += (_, _) =>
        {
            keypad.IsVisible = !keypad.IsVisible;
            customAmount.Content = keypad.IsVisible ? "TASTENFELD SCHLIESSEN" : "ANDERER BETRAG";
            if (keypad.IsVisible)
                _given.Focus();
        };

        var givenPanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "GEGEBEN",
                    FontWeight = FontWeight.Bold,
                    Opacity = 0.75
                },
                _given,
                customAmount,
                keypad
            }
        };

        var tenderButtons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,2.85*"),
            ColumnSpacing = 10,
            Children = { exact, quickGrid }
        };
        Grid.SetColumn(quickGrid, 1);

        var footerButtons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { cancel, _accept }
        };
        Grid.SetColumn(_accept, 1);

        var contentPanel = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "BARZAHLUNG",
                    FontSize = 26,
                    FontWeight = FontWeight.Bold
                },
                BuildAmountRow("ZU ZAHLEN", Formatting.Money(totalCents), 40, Brushes.White),
                new TextBlock
                {
                    Text = "ZAHLBETRAG WÄHLEN",
                    FontWeight = FontWeight.Bold,
                    Opacity = 0.72
                },
                tenderButtons,
                givenPanel,
                BuildAmountRow("RÜCKGELD", "0,00 €", 34, Brushes.LightGreen, _change),
                footerButtons
            }
        };

        Content = new ScrollViewer
        {
            Content = contentPanel,
            Margin = new Avalonia.Thickness(26),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        Opened += (_, _) => _given.Focus();
    }

    private Button CreateTenderButton(
        string text,
        long cents,
        string background = "#263E54",
        string border = "#5C7690")
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 72,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Tag = cents,
            Background = new SolidColorBrush(Color.Parse(background)),
            BorderBrush = new SolidColorBrush(Color.Parse(border))
        };
        button.Click += (_, _) =>
        {
            if (button.Tag is long amount)
                _given.Text = Formatting.Money(amount).Replace(" €", "");
        };
        return button;
    }

    private static Control BuildAmountRow(
        string label,
        string value,
        double valueSize,
        IBrush valueBrush,
        TextBlock? valueTarget = null)
    {
        var valueText = valueTarget ?? new TextBlock();
        valueText.Text = value;
        valueText.FontSize = valueSize;
        valueText.FontWeight = FontWeight.Bold;
        valueText.Foreground = valueBrush;
        valueText.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.72
                },
                valueText
            }
        };
        Grid.SetColumn(valueText, 1);
        return grid;
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

    private void HandleKeypad(string label)
    {
        if (label == "C")
        {
            _given.Text = "";
            return;
        }

        var text = (_given.Text ?? "").Trim();
        if (label == ",")
        {
            if (text.Contains(',') || text.Contains('.'))
                return;
            _given.Text = string.IsNullOrWhiteSpace(text) ? "0," : text + ",";
            return;
        }

        if (text.Length >= 12)
            return;

        _given.Text = text + label;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close((CashPaymentResult?)null);
        }
    }

    private void RefreshChange()
    {
        if (!Formatting.TryParseMoney(_given.Text, out var tendered) || tendered < _totalCents)
        {
            _change.Text = tendered > 0 && tendered < _totalCents
                ? $"FEHLT {Formatting.Money(_totalCents - tendered)}"
                : "0,00 €";
            _change.Foreground = tendered > 0 && tendered < _totalCents
                ? Brushes.Orange
                : Brushes.LightGreen;
            _accept.IsEnabled = false;
            _accept.Content = "KASSIEREN";
            return;
        }

        var change = tendered - _totalCents;
        _change.Text = Formatting.Money(change);
        _change.Foreground = Brushes.LightGreen;
        _accept.IsEnabled = true;
        _accept.Content = "KASSIEREN";
    }

    private void Accept()
    {
        if (!Formatting.TryParseMoney(_given.Text, out var tendered) || tendered < _totalCents)
            return;

        Close((CashPaymentResult?)new CashPaymentResult(
            tendered,
            tendered - _totalCents));
    }
}

// R101: the cash portion the cashier collects for a split cash+card sale.
// The remainder (Total - CashPortionCents) is charged to the card terminal
// exactly, so there is no separate "tendered/change" concept here the way
// CashPaymentWindow has - the entered amount itself IS what changes hands
// in cash, not a tender against it.
public sealed class MixedPaymentWindow : Window
{
    private readonly long _totalCents;
    private readonly TextBox _cashPortion = new()
    {
        FontSize = 26,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        PlaceholderText = "0,00",
        MinHeight = 56
    };

    private readonly TextBlock _cardPortion = new()
    {
        FontSize = 34,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = Brushes.LightSkyBlue
    };

    private readonly TextBlock _hint = new()
    {
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly Button _accept = new()
    {
        MinHeight = 72,
        FontSize = 22,
        FontWeight = FontWeight.Bold,
        Content = "WEITER · ZUR KARTENZAHLUNG",
        IsEnabled = false,
        Background = new SolidColorBrush(Color.Parse("#5B3E8A")),
        BorderBrush = new SolidColorBrush(Color.Parse("#A98AE0"))
    };

    public MixedPaymentWindow(long totalCents)
    {
        _totalCents = totalCents;
        Title = "Gemischte Zahlung";
        Width = 640;
        Height = 520;
        MinWidth = 580;
        MinHeight = 480;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _cashPortion.TextChanged += (_, _) => Refresh();
        _cashPortion.KeyDown += OnInputKeyDown;
        _accept.Click += (_, _) => Accept();

        var cancel = new Button { Content = "ABBRECHEN", MinHeight = 72, FontSize = 18, MinWidth = 180 };
        cancel.Click += (_, _) => Close((long?)null);

        var quickGrid = new UniformGrid { Columns = 3, Rows = 1 };
        foreach (var fraction in new[] { 0.25m, 0.5m, 0.75m })
        {
            var amount = (long)Math.Round(totalCents * fraction, MidpointRounding.AwayFromZero);
            if (amount <= 0 || amount >= totalCents) continue;
            var button = new Button
            {
                Content = $"{fraction * 100:0} % BAR\n{Formatting.Money(amount)}",
                MinHeight = 72,
                FontSize = 16,
                FontWeight = FontWeight.Bold,
                Tag = amount
            };
            button.Click += (_, _) => _cashPortion.Text = Formatting.Money(amount).Replace(" €", "");
            quickGrid.Children.Add(button);
        }

        var footerButtons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { cancel, _accept }
        };
        Grid.SetColumn(_accept, 1);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "GEMISCHTE ZAHLUNG", FontSize = 26, FontWeight = FontWeight.Bold },
                    BuildAmountRow("GESAMT", Formatting.Money(totalCents), 40, Brushes.White),
                    new TextBlock { Text = "BAR-ANTEIL EINGEBEN", FontWeight = FontWeight.Bold, Opacity = 0.72 },
                    quickGrid,
                    new StackPanel
                    {
                        Spacing = 8,
                        Children = { new TextBlock { Text = "BAR-ANTEIL", FontWeight = FontWeight.Bold, Opacity = 0.75 }, _cashPortion }
                    },
                    BuildAmountRow("KARTEN-ANTEIL (Rest)", "0,00 €", 34, Brushes.LightSkyBlue, _cardPortion),
                    _hint,
                    footerButtons
                }
            },
            Margin = new Avalonia.Thickness(26),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        Opened += (_, _) => _cashPortion.Focus();
    }

    private static Control BuildAmountRow(string label, string value, double valueSize, IBrush valueBrush, TextBlock? valueTarget = null)
    {
        var valueText = valueTarget ?? new TextBlock();
        valueText.Text = value;
        valueText.FontSize = valueSize;
        valueText.FontWeight = FontWeight.Bold;
        valueText.Foreground = valueBrush;
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 16,
            Children =
            {
                new TextBlock { Text = label, FontSize = 17, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72 },
                valueText
            }
        };
        Grid.SetColumn(valueText, 1);
        return grid;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close((long?)null); }
    }

    private void Refresh()
    {
        if (!Formatting.TryParseMoney(_cashPortion.Text, out var cash) || cash <= 0 || cash >= _totalCents)
        {
            _cardPortion.Text = "0,00 €";
            _hint.Text = "Bar-Anteil muss größer als 0 und kleiner als der Gesamtbetrag sein. Für eine reine Bar- oder Kartenzahlung BAR bzw. KARTE verwenden.";
            _hint.Foreground = Brushes.Orange;
            _accept.IsEnabled = false;
            return;
        }

        _cardPortion.Text = Formatting.Money(_totalCents - cash);
        _hint.Text = "Der Bar-Anteil wird sofort kassiert, der Karten-Anteil wird anschließend am Kartenterminal belastet.";
        _hint.Foreground = Brushes.Gray;
        _accept.IsEnabled = true;
    }

    private void Accept()
    {
        if (!Formatting.TryParseMoney(_cashPortion.Text, out var cash) || cash <= 0 || cash >= _totalCents)
            return;
        Close((long?)cash);
    }
}

public sealed class ReceiptModeWindow : Window
{
    public ReceiptModeWindow(bool currentEnabled)
    {
        Title = "Bon-Ausgabe";
        Width = 520;
        Height = 300;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var on = new Button
        {
            Content = "BON EIN",
            MinHeight = 78,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Background = currentEnabled ? new SolidColorBrush(Color.Parse("#0E7A43")) : AppTheme.ButtonNeutral,
            BorderBrush = currentEnabled ? new SolidColorBrush(Color.Parse("#53E0A1")) : AppTheme.ButtonNeutralBorder,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10)
        };
        on.Click += (_, _) => Close((bool?)true);

        var off = new Button
        {
            Content = "BON AUS",
            MinHeight = 78,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Background = currentEnabled ? AppTheme.ButtonNeutral : new SolidColorBrush(Color.Parse("#7A2932")),
            BorderBrush = currentEnabled ? AppTheme.ButtonNeutralBorder : new SolidColorBrush(Color.Parse("#D86A77")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10)
        };
        off.Click += (_, _) => Close((bool?)false);

        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Children = { on, off }
        };
        Grid.SetColumn(off, 1);

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 44
        };
        cancel.Click += (_, _) => Close((bool?)null);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "BON EIN / AUS",
                    FontSize = 23,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = currentEnabled
                        ? "Automatischer Bondruck ist aktuell EIN."
                        : "Automatischer Bondruck ist aktuell AUS.",
                    Opacity = 0.70
                },
                buttons,
                cancel
            }
        };
    }
}

public sealed record PfandOption(
    long ProductId,
    string Name,
    long PriceCents);

/// <summary>R149: what the PFAND / LEERGUT key takes back, at which VAT rate.</summary>
public sealed record PfandReturnSelection(PfandOption Option, decimal VatRate);

/// <summary>
/// R149: returned empties (Leergut). The customer receives the deposit, so the
/// position is negative - never a sale. A bottle's deposit takes the rate of the
/// drink it held (Warenumschließung): 19 % for beverages, 7 % for milk and milk
/// drinks; a crate is a Transporthilfsmittel and always 19 % (DSFinV-K Anhang C,
/// § 12 Abs. 1 UStG).
/// </summary>
public sealed class PfandSelectionWindow : Window
{
    private readonly PfandOption[] _options;
    private bool _reducedRate;
    private readonly Button _beverages;
    private readonly Button _milk;

    public PfandSelectionWindow(
        long pfand8 = 8,
        long pfand15 = 15,
        long pfand25 = 25,
        long crateEmpty = 150,
        long crateFull = 330)
    {
        _options =
        [
            new(PfandProducts.Bottle8, "PFAND-RÜCKGABE · 8 CENT", Math.Max(0, pfand8)),
            new(PfandProducts.Bottle15, "PFAND-RÜCKGABE · 15 CENT", Math.Max(0, pfand15)),
            new(PfandProducts.Bottle25, "PFAND-RÜCKGABE · 25 CENT", Math.Max(0, pfand25)),
            new(PfandProducts.CrateEmpty, "LEERGUT KISTE LEER", Math.Max(0, crateEmpty)),
            new(PfandProducts.CrateFull, "LEERGUT KISTE VOLL", Math.Max(0, crateFull))
        ];
        Title = "Pfand-Rückgabe / Leergut";
        Width = 600;
        Height = 660;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _beverages = RateButton("GETRÄNKE · 19 %");
        _milk = RateButton("MILCH / MILCHGETRÄNK · 7 %");
        _beverages.Click += (_, _) => SelectRate(false);
        _milk.Click += (_, _) => SelectRate(true);

        var rates = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10
        };
        Grid.SetColumn(_milk, 1);
        rates.Children.Add(_beverages);
        rates.Children.Add(_milk);

        var optionGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("80,80,80"),
            ColumnSpacing = 10,
            RowSpacing = 10
        };

        for (var index = 0; index < _options.Length; index++)
        {
            var option = _options[index];
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = option.Name,
                        FontSize = 14,
                        FontWeight = FontWeight.Bold,
                        TextAlignment = TextAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = Formatting.Money(-option.PriceCents),
                        FontSize = 18,
                        FontWeight = FontWeight.Bold,
                        Foreground = AppTheme.WarningAmber,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            };
            if (PfandProducts.IsCrate(option.ProductId))
                content.Children.Add(new TextBlock
                {
                    Text = "Kiste: immer 19 %",
                    FontSize = 11,
                    Foreground = AppTheme.TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

            var button = new Button
            {
                Tag = option,
                MinHeight = 80,
                Background = AppTheme.ButtonNeutral,
                BorderBrush = AppTheme.ButtonNeutralBorder,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = content
            };

            button.Click += (_, _) => Close((PfandReturnSelection?)new PfandReturnSelection(
                option, PfandProducts.RateFor(option.ProductId, _reducedRate)));
            Grid.SetColumn(button, index % 2);
            Grid.SetRow(button, index / 2);
            optionGrid.Children.Add(button);
        }

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 50,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancel.Click += (_, _) => Close((PfandReturnSelection?)null);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "LEERGUT ZURÜCKNEHMEN",
                    FontSize = 23,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Der Kunde gibt Leergut zurück. Der Pfandbetrag wird abgezogen; ist er höher als der Einkauf, wird die Differenz bar ausgezahlt. Menge vorher über die Zifferntasten eingeben.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                },
                new TextBlock
                {
                    Text = "Flaschenpfand: Steuersatz des Getränks",
                    FontSize = 13,
                    FontWeight = FontWeight.Bold
                },
                rates,
                optionGrid,
                cancel
            }
        };

        SelectRate(false);
    }

    private static Button RateButton(string text) => new()
    {
        Content = text,
        MinHeight = 48,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Avalonia.Thickness(2),
        CornerRadius = new Avalonia.CornerRadius(10)
    };

    private void SelectRate(bool reduced)
    {
        _reducedRate = reduced;
        foreach (var (button, selected) in new[] { (_beverages, !reduced), (_milk, reduced) })
        {
            button.Background = selected ? AppTheme.InfoBlue : AppTheme.ButtonNeutral;
            button.BorderBrush = selected ? AppTheme.InfoBlueBorder : AppTheme.ButtonNeutralBorder;
            button.Foreground = Brushes.White;
        }
    }
}

/// <summary>
/// R149: the returned deposit exceeds the purchase - the difference is paid out in
/// cash. Booked as a receipt with a negative total (PfandRueckzahlung).
/// </summary>
public sealed class DepositPayoutWindow : Window
{
    public DepositPayoutWindow(long payoutCents)
    {
        Title = "Pfand auszahlen";
        Width = 560;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "ABBRECHEN", MinWidth = 160, MinHeight = 60, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        cancel.Click += (_, _) => Close(false);
        var paid = new Button
        {
            Content = "AUSGEZAHLT",
            MinWidth = 220,
            MinHeight = 60,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = AppTheme.SuccessGreen,
            BorderBrush = AppTheme.SuccessGreenBorder,
            Foreground = Brushes.White,
            IsDefault = true
        };
        paid.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "PFAND AUSZAHLEN", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock { Text = Formatting.Money(payoutCents), FontSize = 40, FontWeight = FontWeight.Bold, Foreground = AppTheme.WarningAmber },
                new TextBlock
                {
                    Text = "Diesen Betrag bar an den Kunden auszahlen. Gebucht wird er als Pfand-Rückzahlung.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, paid }
                }
            }
        };
    }
}

public sealed class VariantEditWindow:Window
{
    private readonly TextBox _name=new();
    private readonly TextBox _price=new();

    public VariantEditWindow(ProductVariant? current=null)
    {
        Title="Variante";Width=440;Height=280;CanResize=false;
        if(current is not null)
        {
            _name.Text=current.Name;
            _price.Text=(current.PriceCents/100m).ToString("0.00",System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        }

        var save=new Button{Content="SPEICHERN",MinHeight=48};
        save.Click+=(_,_)=>Save();

        Content=new StackPanel
        {
            Margin=new Avalonia.Thickness(24),Spacing=8,
            Children=
            {
                new TextBlock{Text="Name, z.B. Klein 26 cm"},_name,
                new TextBlock{Text="Preis €"},_price,save
            }
        };
    }

    private void Save()
    {
        if(string.IsNullOrWhiteSpace(_name.Text))return;
        if(!Formatting.TryParseMoney(_price.Text,out var c))return;
        Close((ProductVariant?)new ProductVariant(0,0,_name.Text.Trim(),c));
    }
}


public sealed class EanSearchWindow : Window
{
    private readonly TextBox _ean = new()
    {
        FontSize = 22,
        MinHeight = 48,
        PlaceholderText = "EAN / Barcode eingeben"
    };

    public EanSearchWindow()
    {
        Title = "EAN suchen";
        Width = 480;
        Height = 245;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _ean.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
        };

        var search = new Button
        {
            Content = "SUCHEN",
            MinHeight = 50,
            MinWidth = 150
        };
        search.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 50,
            MinWidth = 150
        };
        cancel.Click += (_, _) => Close((string?)null);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "EAN / Barcode suchen",
                    FontSize = 21,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Nur für manuelle Suche. Im Kassenfenster können Artikel jederzeit direkt gescannt werden.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65
                },
                _ean,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, search }
                }
            }
        };

        Opened += (_, _) => _ean.Focus();
    }

    private void Accept()
    {
        var code = (_ean.Text ?? "").Trim();
        if (code.Length == 0)
            return;

        Close((string?)code);
    }
}


public sealed record ParkedReceiptDialogResult(long Id, bool Delete);

public sealed class ParkedReceiptsWindow : Window
{
    private readonly ListBox _list = new();

    public ParkedReceiptsWindow(IReadOnlyList<ParkedReceipt> receipts, bool orderMode = false)
    {
        Title = orderMode ? "Offene Bestellungen" : "Geparkte Bons";
        Width = 760;
        Height = 560;
        MinWidth = 650;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Opened += (_,_) => UiLanguage.Apply(this);

        _list.ItemsSource = receipts
            .Select(x => new ParkedRow(
                x.Id,
                x.DisplayNumber,
                x.PickupNumber,
                x.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm"),
                x.Lines.Count,
                x.QuantityTotal,
                x.TotalCents,
                x.CreatedBy))
            .ToArray();

        var restore = new Button
        {
            Content = orderMode ? "BESTELLUNG AUFRUFEN" : "BON ÜBERNEHMEN",
            MinWidth = 190,
            MinHeight = 50,
            FontWeight = FontWeight.Bold
        };
        restore.Click += (_, _) => RestoreSelected();

        var delete = new Button
        {
            Content = orderMode ? "BESTELLUNG STORNIEREN" : "BON LÖSCHEN",
            MinWidth = 160,
            MinHeight = 50,
            Foreground = Brushes.White,
            Background = AppTheme.DangerRedBorder
        };
        delete.Click += (_, _) => DeleteSelected();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinWidth = 140,
            MinHeight = 50
        };
        close.Click += (_, _) => Close((ParkedReceiptDialogResult?)null);

        _list.DoubleTapped += (_, _) => RestoreSelected();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Avalonia.Thickness(20),
            RowSpacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = orderMode ? "Offene Bestellungen" : "Geparkte Bons",
                            FontSize = 25,
                            FontWeight = FontWeight.Bold
                        },
                        new TextBlock
                        {
                            Text = orderMode
                                ? "Bestellung mit Abholnummer auswählen und BESTELLUNG AUFRUFEN. Zahlung und Bon entstehen erst mit BAR oder KARTE."
                                : "Offene geparkte Bons müssen vor dem Z-Abschluss kassiert werden.",
                            Foreground = Brushes.Orange,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { close, delete, restore }
                }
            }
        };

        Grid.SetRow(_list, 1);
        if (Content is Grid grid)
            Grid.SetRow(grid.Children[2], 2);
    }

    private void RestoreSelected()
    {
        if (_list.SelectedItem is not ParkedRow row)
            return;

        Close((ParkedReceiptDialogResult?)new ParkedReceiptDialogResult(row.Id, false));
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItem is not ParkedRow row)
            return;

        Close((ParkedReceiptDialogResult?)new ParkedReceiptDialogResult(row.Id, true));
    }

    private sealed record ParkedRow(
        long Id,
        string Bon,
        long PickupNumber,
        string Zeit,
        int Positionen,
        decimal Menge,
        long TotalCents,
        string Bediener)
    {
        public override string ToString() =>
            $"{(PickupNumber>0 ? $"ABHOLNR. {PickupNumber:000}   " : "")}{Bon}   {Zeit}   {Positionen} Pos. / {Menge:0.##} Stk.   " +
            $"{Formatting.Money(TotalCents)}" +
            (string.IsNullOrWhiteSpace(Bediener) ? "" : $"   · {Bediener}");
    }
}


public sealed class CashMovementWindow : Window
{
    private readonly ComboBox _type = new()
    {
        ItemsSource = new[] { "EINLAGE", "ENTNAHME" },
        SelectedIndex = 0,
        MinHeight = 42
    };

    // R134: what the movement is (AEAO zu § 146a Nr. 1.10.2) - Geldtransit,
    // Privateinlage/-entnahme, Lohnzahlung or another cash flow.
    private readonly ComboBox _businessCase = new()
    {
        MinHeight = 42
    };

    private readonly TextBlock _hint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75
    };

    private readonly TextBox _amount = new()
    {
        FontSize = 20,
        PlaceholderText = "Betrag in €"
    };

    private readonly TextBox _reason = new()
    {
        PlaceholderText = "Grund / Belegtext"
    };

    private sealed record CaseItem(CashBusinessCase Case, string Label)
    {
        public override string ToString() => Label;
    }

    private CashMovementKind SelectedKind =>
        _type.SelectedIndex == 1 ? CashMovementKind.Entnahme : CashMovementKind.Einlage;

    private void RefreshBusinessCases()
    {
        var kind = SelectedKind;
        _businessCase.ItemsSource = CashBusinessCases.For(kind)
            .Select(c => new CaseItem(c, CashBusinessCases.Label(c, kind)))
            .ToArray();
        _businessCase.SelectedIndex = -1;
        _hint.Text = "Bitte die Art wählen.";
    }

    public CashMovementWindow()
    {
        Title = "Kassenbewegung";
        Width = 520;
        Height = 440;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button
        {
            Content = "BUCHEN",
            MinWidth = 150,
            MinHeight = 50,
            FontWeight = FontWeight.Bold
        };
        save.Click += (_, _) => Save();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinWidth = 140,
            MinHeight = 50
        };
        cancel.Click += (_, _) => Close((CashMovementRequest?)null);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Kassenbewegung",
                    FontSize = 23,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Einlagen und Entnahmen sind Geschäftsvorfälle: Art und Grund werden erfasst und von der TSE abgesichert.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65
                },
                _type,
                _businessCase,
                _hint,
                _amount,
                _reason,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, save }
                }
            }
        };

        _type.SelectionChanged += (_, _) => RefreshBusinessCases();
        _businessCase.SelectionChanged += (_, _) => _hint.Text = "";
        RefreshBusinessCases();

        Opened += (_, _) => _amount.Focus();
    }

    private void Save()
    {
        if (_businessCase.SelectedItem is not CaseItem chosen)
        {
            _hint.Text = "Bitte die Art der Kassenbewegung wählen.";
            return;
        }

        if (!Formatting.TryParseMoney(_amount.Text, out var cents))
            return;

        if (cents <= 0 || string.IsNullOrWhiteSpace(_reason.Text))
            return;

        Close((CashMovementRequest?)new CashMovementRequest(
            SelectedKind,
            cents,
            _reason.Text.Trim(),
            chosen.Case));
    }
}


public sealed class CardTestPaymentWindow : Window
{
    public CardTestPaymentWindow(long totalCents)
    {
        Title = "KARTENZAHLUNG · TEST";
        Width = 620;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinWidth = 170,
            MinHeight = 58
        };
        cancel.Click += (_, _) => Close(false);

        var confirm = new Button
        {
            Content = "KARTE BESTÄTIGEN",
            MinWidth = 220,
            MinHeight = 58,
            FontWeight = FontWeight.Bold
        };
        confirm.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(30),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "KARTENZAHLUNG · TEST",
                    FontSize = 27,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = $"ZU ZAHLEN: {Formatting.Money(totalCents)}",
                    FontSize = 30,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Testsimulation – keine echte Terminalbuchung. " +
                           "Der Bon wird erst nach BESTÄTIGEN abgeschlossen.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 16,
                    Opacity = 0.75
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 14,
                    Children = { cancel, confirm }
                }
            }
        };
    }
}

public sealed class ZReportInfoWindow : Window
{
    public ZReportInfoWindow(
        string title,
        string message,
        bool ready)
    {
        Title = title;
        Width = 620;
        Height = 330;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = "OK",
            MinWidth = 150,
            MinHeight = 52,
            FontWeight = FontWeight.Bold
        };
        close.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 25,
                    FontWeight = FontWeight.Bold,
                    Foreground = ready ? Brushes.LightGreen : Brushes.Orange
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 16
                },
                new TextBlock
                {
                    Text = "TOR-Regel: Offene geparkte Bons sperren den Z-Abschluss.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65
                },
                close
            }
        };
    }
}

public sealed class ExtraSelectionWindow : Window
{
    private readonly ListBox _list = new();

    public ExtraSelectionWindow(IReadOnlyList<ExtraItem> extras)
    {
        Title = "Extra auswählen";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemsSource = extras
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new ExtraRow(x))
            .ToArray();
        _list.DoubleTapped += (_,_) => Accept();

        var add = new Button
        {
            Content = "EXTRA HINZUFÜGEN",
            MinWidth = 190,
            MinHeight = 52,
            FontWeight = FontWeight.Bold
        };
        add.Click += (_,_) => Accept();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinWidth = 140,
            MinHeight = 52
        };
        cancel.Click += (_,_) => Close((ExtraItem?)null);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Avalonia.Thickness(22),
            RowSpacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "EXTRA",
                            FontSize = 25,
                            FontWeight = FontWeight.Bold
                        },
                        new TextBlock
                        {
                            Text = "Das gewählte Extra wird als eigene Position zum aktuellen Verkauf hinzugefügt.",
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.68
                        }
                    }
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, add }
                }
            }
        };

        Grid.SetRow(_list, 1);
        if (Content is Grid grid)
            Grid.SetRow(grid.Children[2], 2);

        if (extras.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is ExtraRow row)
            Close((ExtraItem?)row.Item);
    }

    private sealed record ExtraRow(ExtraItem Item)
    {
        public override string ToString() =>
            $"{Item.Name}   {Formatting.Money(Item.PriceCents)}   · MwSt. {Item.VatRate:0} %";
    }
}

public enum ReceiptHistoryAction
{
    PrintCopy,
    FullStorno,
    PartialReturn
}

public sealed record ReceiptHistorySelection(long SaleId, ReceiptHistoryAction Action);

public sealed class ReceiptHistoryWindow : Window
{
    private readonly ISaleRepository _repository;
    private readonly BusinessManagementService _management;
    private readonly bool _stornoMode;
    private readonly bool _returnMode;
    private readonly StackPanel _rows = new() { Spacing = 7 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _archiveFilters = new() { Spacing = 8, IsVisible = false };

    public ReceiptHistoryWindow(
        ISaleRepository repository,
        BusinessManagementService management,
        bool stornoMode = false,
        bool returnMode = false)
    {
        _repository = repository;
        _management = management;
        _stornoMode = stornoMode;
        _returnMode = returnMode;

        Title = stornoMode
            ? "BON STORNO · Heute"
            : returnMode
                ? "TEILRETOURE · Heute"
                : "BON-HISTORIE · Heute";
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = new TextBox { Text = today.ToString("dd.MM.yyyy"), MinWidth = 135 };
        var to = new TextBox { Text = today.ToString("dd.MM.yyyy"), MinWidth = 135 };
        var number = new TextBox { PlaceholderText = "Bonnummer (optional)", MinWidth = 180 };
        var method = new ComboBox
        {
            ItemsSource = new[] { "Alle Zahlarten", "Bar", "Karte", "Gemischt" },
            SelectedIndex = 0,
            MinWidth = 160
        };
        var archiveSearch = new Button { Content = "ARCHIV SUCHEN", MinHeight = 42, MinWidth = 150 };

        _archiveFilters.Children.Add(new TextBlock
        {
            Text = "ARCHIV · Ältere Bons können angesehen oder als Kopie gedruckt werden. STORNO / TEILRETOURE ist ausschließlich am Verkaufstag möglich.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.WarningAmber
        });
        _archiveFilters.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = "Von: ", VerticalAlignment = VerticalAlignment.Center }, from,
                new TextBlock { Text = " Bis: ", VerticalAlignment = VerticalAlignment.Center }, to
            }
        });
        _archiveFilters.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { number, method, archiveSearch }
        });

        var refresh = new Button { Content = "HEUTE AKTUALISIEREN", MinHeight = 42, MinWidth = 170 };
        refresh.Click += async (_, _) => await LoadTodayAsync();

        var archiveToggle = new Button { Content = "ARCHIV / SUCHE", MinHeight = 42, MinWidth = 150 };
        archiveToggle.Click += (_, _) =>
        {
            _archiveFilters.IsVisible = !_archiveFilters.IsVisible;
            archiveToggle.Content = _archiveFilters.IsVisible ? "ARCHIV SCHLIESSEN" : "ARCHIV / SUCHE";
        };

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 42, MinWidth = 130 };
        close.Click += (_, _) => Close((ReceiptHistorySelection?)null);

        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { refresh, archiveToggle, close }
        };

        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#102235")),
            BorderBrush = new SolidColorBrush(Color.Parse("#294765")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(14),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = stornoMode ? "BON STORNO · HEUTE" : returnMode ? "TEILRETOURE · HEUTE" : "BON-HISTORIE · HEUTE",
                        FontSize = 24,
                        FontWeight = FontWeight.Bold,
                        Foreground = AppTheme.AccentTeal
                    },
                    new TextBlock
                    {
                        Text = $"Heute {today:dd.MM.yyyy} · alle gespeicherten Bons werden automatisch geladen.",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#A9BDCF"))
                    },
                    new TextBlock
                    {
                        Text = stornoMode
                            ? "Nur reguläre Verkäufe von heute können vollständig storniert werden."
                            : returnMode
                                ? "Nur reguläre Verkäufe von heute können teilweise retourniert werden."
                                : "Direkt am Bon: ANZEIGEN · KOPIE DRUCKEN · BON STORNIEREN · TEILRETOURE.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.72
                    }
                }
            }
        };

        var listHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("105,130,100,110,*,Auto"),
            Margin = new Avalonia.Thickness(2, 0, 2, 3)
        };
        AddHeader(listHeader, 0, "BON");
        AddHeader(listHeader, 1, "ZEIT");
        AddHeader(listHeader, 2, "ZAHLART");
        AddHeader(listHeader, 3, "SUMME");
        AddHeader(listHeader, 4, "BEDIENER / STATUS");
        AddHeader(listHeader, 5, "AKTIONEN");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _rows
        };

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
            Margin = new Avalonia.Thickness(18),
            RowSpacing = 9,
            Children = { header, toolbar, _archiveFilters, _status, new StackPanel { Spacing = 4, Children = { listHeader, scroll } } }
        };
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(_archiveFilters, 2);
        Grid.SetRow(_status, 3);
        Grid.SetRow(body.Children[4], 4);
        Content = body;

        archiveSearch.Click += async (_, _) =>
        {
            try
            {
                if (!DateOnly.TryParseExact(
                        from.Text?.Trim(),
                        "dd.MM.yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var start) ||
                    !DateOnly.TryParseExact(
                        to.Text?.Trim(),
                        "dd.MM.yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var end))
                    throw new ArgumentException("Datum als TT.MM.JJJJ eingeben.");

                long? receiptNumber = null;
                if (!string.IsNullOrWhiteSpace(number.Text))
                {
                    if (!long.TryParse(number.Text.Trim(), out var value) || value <= 0)
                        throw new ArgumentException("Eine positive Bonnummer eingeben oder das Feld leeren.");
                    receiptNumber = value;
                }

                var payment = method.SelectedIndex switch
                {
                    1 => PaymentMethod.Cash,
                    2 => PaymentMethod.Card,
                    3 => PaymentMethod.Mixed,
                    _ => (PaymentMethod?)null
                };

                _status.Text = "Archiv wird geladen ...";
                var found = await _repository.SearchHistoryAsync(start, end, receiptNumber, payment);
                RenderRows(found, todayOnlyActions: false);
                _status.Text = found.Count == 0
                    ? "Keine gespeicherten Bons für diesen Archivfilter gefunden."
                    : $"{found.Count} Bon(s) im Archiv gefunden. Alte Bons: nur Anzeigen / Kopie.";
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Receipt archive search", ex);
                _status.Text = "ARCHIV: " + ex.Message;
            }
        };

        Opened += async (_, _) =>
        {
            UiLanguage.Apply(this);
            await LoadTodayAsync();
        };
    }

    private static void AddHeader(Grid grid, int column, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#8EA8BE")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private async Task LoadTodayAsync()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            _status.Text = "Heutige Bons werden geladen ...";
            var found = await _repository.SearchHistoryAsync(today, today);
            RenderRows(found, todayOnlyActions: true);
            _status.Text = found.Count == 0
                ? $"Heute {today:dd.MM.yyyy} wurden noch keine echten Bons gespeichert."
                : $"HEUTE · {today:dd.MM.yyyy} · {found.Count} Bon(s) · neueste zuerst";
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Receipt history today", ex);
            _status.Text = "BON-HISTORIE: " + ex.Message;
        }
    }

    private void RenderRows(IReadOnlyList<Sale> sales, bool todayOnlyActions)
    {
        _rows.Children.Clear();
        var today = DateTimeOffset.Now.Date;

        foreach (var sale in sales)
        {
            var isToday = sale.CreatedAt.Date == today;
            var regularSale = string.Equals(sale.TransactionType, "SALE", StringComparison.OrdinalIgnoreCase);
            var reversalAllowedByDate = todayOnlyActions && isToday && regularSale;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("105,130,100,110,*,Auto"),
                MinHeight = 58,
                Margin = new Avalonia.Thickness(0, 0, 0, 2),
                Background = new SolidColorBrush(Color.Parse("#102033"))
            };

            AddCell(row, 0, $"#{sale.ReceiptNumber:000000}", FontWeight.Bold);
            AddCell(row, 1, sale.CreatedAt.LocalDateTime.ToString("dd.MM. HH:mm:ss"));
            AddCell(row, 2, sale.PaymentMethod switch
            {
                PaymentMethod.Card => "KARTE",
                PaymentMethod.Mixed => "BAR/KARTE",
                _ => "BAR"
            });
            AddCell(row, 3, Formatting.Money(sale.TotalCents), FontWeight.Bold);

            var status = string.Equals(sale.TransactionType, "STORNO", StringComparison.OrdinalIgnoreCase)
                ? "STORNO"
                : string.Equals(sale.TransactionType, "RETURN", StringComparison.OrdinalIgnoreCase)
                    ? "RETOURE"
                    : "VERKAUF";
            AddCell(
                row,
                4,
                (string.IsNullOrWhiteSpace(sale.OperatorName) ? "—" : sale.OperatorName) + " · " + status,
                status == "VERKAUF" ? FontWeight.Normal : FontWeight.Bold);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Avalonia.Thickness(8, 7)
            };
            Grid.SetColumn(actions, 5);

            var view = ActionButton("ANZEIGEN");
            view.Click += async (_, _) => await ShowPreviewAsync(sale);
            actions.Children.Add(view);

            var copy = ActionButton("KOPIE");
            copy.Click += (_, _) => Close(new ReceiptHistorySelection(sale.Id, ReceiptHistoryAction.PrintCopy));
            actions.Children.Add(copy);

            if (!_returnMode)
            {
                var storno = ActionButton("STORNO", danger: true);
                storno.IsEnabled = reversalAllowedByDate;
                storno.Click += (_, _) => Close(new ReceiptHistorySelection(sale.Id, ReceiptHistoryAction.FullStorno));
                actions.Children.Add(storno);
            }

            if (!_stornoMode)
            {
                var retoure = ActionButton("TEILRETOURE", danger: true);
                retoure.IsEnabled = reversalAllowedByDate;
                retoure.Click += (_, _) => Close(new ReceiptHistorySelection(sale.Id, ReceiptHistoryAction.PartialReturn));
                actions.Children.Add(retoure);
            }

            if (_stornoMode)
            {
                copy.IsVisible = false;
                view.IsVisible = true;
            }
            else if (_returnMode)
            {
                copy.IsVisible = false;
                view.IsVisible = true;
            }

            row.Children.Add(actions);
            _rows.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse(
                    status == "STORNO" ? "#7D3D47" :
                    status == "RETOURE" ? "#775F25" : "#294765")),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Child = row
            });
        }
    }

    private static void AddCell(Grid grid, int column, string text, FontWeight? weight = null)
    {
        var cell = new TextBlock
        {
            Text = text,
            Margin = new Avalonia.Thickness(10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            FontWeight = weight ?? FontWeight.Normal
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static Button ActionButton(string text, bool danger = false)
    {
        return new Button
        {
            Content = text,
            MinHeight = 34,
            MinWidth = text == "TEILRETOURE" ? 100 : 72,
            Padding = new Avalonia.Thickness(8, 5),
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Background = new SolidColorBrush(Color.Parse(danger ? "#54252E" : "#1C3852")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse(danger ? "#9D5260" : "#496985")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6)
        };
    }

    private async Task ShowPreviewAsync(Sale sale)
    {
        var lines = new List<string>
        {
            "BON-KOPIE · DATENANSICHT (kein neuer Verkauf)",
            $"Bon {sale.ReceiptNumber:000000} · {sale.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}",
            sale.PickupNumber > 0 ? $"Abholnummer: {sale.PickupNumber:000}" : "Abholnummer: –",
            $"Bediener: {sale.OperatorName} · Zahlung: {sale.PaymentMethod}",
            $"Art: {sale.TransactionType} · Fiskalstatus: {sale.FiscalStatus}", ""
        };
        lines.AddRange(sale.Lines.Select(x =>
            $"{x.Quantity} × {x.ProductName} {x.VariantName} · {Formatting.Money(x.LineTotalCents)} · MwSt {x.VatRate}%"));
        lines.Add($"Rabatt: {Formatting.Money(sale.DiscountCents)}");
        lines.Add($"GESAMT: {Formatting.Money(sale.TotalCents)}");
        await new TextReportWindow(
            _management,
            new ReportDocument("BON-KOPIE", lines, DateTimeOffset.Now),
            "Datenansicht des gespeicherten Verkaufs. Keine nachträglich erzeugte TSE-Signatur.")
            .ShowDialog(this);
    }
}


public sealed class PartialReturnWindow : Window
{
    private readonly Dictionary<long, TextBox> _quantityByLine = new();
    private readonly Sale _sale;

    public PartialReturnWindow(Sale sale)
    {
        _sale = sale;
        Title = $"Retoure · Bon {sale.ReceiptNumber:000000}";
        Width = 640;
        Height = 640;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var rows = new StackPanel { Spacing = 6 };
        // R149: returned deposit was paid out and is not handed back as a Retoure.
        foreach (var line in sale.Lines.Where(l => !PfandProducts.IsDepositReturn(l)))
        {
            var qty = new TextBox { Text = "0", Width = 90, MinHeight = 40 };
            _quantityByLine[line.SaleItemId] = qty;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Margin = new Avalonia.Thickness(0, 2)
            };
            var label = new TextBlock
            {
                Text = line.ProductName + (string.IsNullOrWhiteSpace(line.VariantName) ? "" : " · " + line.VariantName) +
                    $"  ·  {Formatting.Money(line.UnitPriceCents)}/Stk.",
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            var original = new TextBlock
            {
                Text = $"von {line.Quantity}",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.65,
                Margin = new Avalonia.Thickness(10, 0)
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(original, 1);
            Grid.SetColumn(qty, 2);
            row.Children.Add(label);
            row.Children.Add(original);
            row.Children.Add(qty);
            rows.Children.Add(row);
        }

        var scroll = new ScrollViewer { Content = rows };

        var confirm = new Button
        {
            Content = "RETOURE BUCHEN",
            MinWidth = 190,
            MinHeight = 52,
            FontWeight = FontWeight.Bold
        };
        confirm.Click += (_, _) =>
        {
            try
            {
                var requests = new List<ReturnLineRequest>();
                foreach (var (saleItemId, box) in _quantityByLine)
                {
                    var text = (box.Text ?? "").Trim().Replace(',', '.');
                    if (text.Length == 0 || text == "0") continue;
                    if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
                        throw new InvalidOperationException("Bitte nur positive Mengen eingeben.");
                    requests.Add(new ReturnLineRequest(saleItemId, value));
                }
                if (requests.Count == 0)
                {
                    status.Text = "Bitte mindestens eine Menge größer als 0 eingeben.";
                    return;
                }
                Close((IReadOnlyList<ReturnLineRequest>?)requests);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        };

        var cancel = new Button { Content = "ABBRECHEN", MinWidth = 140, MinHeight = 52 };
        cancel.Click += (_, _) => Close((IReadOnlyList<ReturnLineRequest>?)null);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Avalonia.Thickness(22),
            RowSpacing = 12,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "TEILRETOURE", FontSize = 25, FontWeight = FontWeight.Bold },
                        new TextBlock
                        {
                            Text = "Menge je Position eingeben, die zurückgenommen werden soll. Bereits zurückgenommene Mengen werden serverseitig erneut geprüft.",
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.68
                        }
                    }
                },
                scroll,
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, confirm }
                }
            }
        };

        Grid.SetRow(scroll, 1);
        Grid.SetRow(status, 2);
        if (Content is Grid grid)
            Grid.SetRow(grid.Children[3], 3);
    }
}

/// <summary>R139: what the cashier decided after seeing Soll, Ist and the difference.</summary>
public sealed record CashCountDecision(bool Recount, string Note);

/// <summary>
/// R139: a Kassensturz is confirmed before it is recorded. A difference is booked
/// as DifferenzSollIst (DSFinV-K Anhang C) - so the cashier can count again
/// first, and is reminded that cash taken out without a booking (e.g. to the
/// bank) is booked as Entnahme/Geldtransit, not as a difference.
/// </summary>
public sealed class CashCountConfirmWindow : Window
{
    public CashCountConfirmWindow(long expectedCents, long countedCents, bool production)
    {
        Title = "Kassensturz bestätigen";
        Width = 640;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var difference = countedCents - expectedCents;
        var note = new TextBox { PlaceholderText = "Bemerkung (optional), z. B. Zählfehler Wechselgeld", FontSize = 16 };

        var cancel = new Button { Content = "ABBRECHEN", MinWidth = 140, MinHeight = 52 };
        cancel.Click += (_, _) => Close(null);
        var recount = new Button { Content = "NEU ZÄHLEN", MinWidth = 150, MinHeight = 52 };
        recount.Click += (_, _) => Close(new CashCountDecision(true, ""));
        var book = new Button
        {
            Content = difference == 0 ? "BESTÄTIGEN" : "DIFFERENZ BUCHEN",
            MinWidth = 200,
            MinHeight = 52,
            Background = difference == 0 ? AppTheme.SuccessGreen : AppTheme.DangerRed,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        book.Click += (_, _) => Close(new CashCountDecision(false, note.Text ?? ""));

        var text = difference == 0
            ? "Soll und Ist stimmen überein. Der gezählte Bestand wird als Kassensturz festgehalten."
            : (difference > 0 ? "Überschuss" : "Fehlbetrag") +
              " - die Differenz wird als Geschäftsvorfall \"DifferenzSollIst\" gebucht" +
              (production ? " und von der TSE abgesichert." : " (Testbetrieb, keine TSE).") +
              " Wurde Bargeld ohne Buchung entnommen (z. B. zur Bank), zuerst eine Entnahme " +
              "\"Geldtransit\" buchen und dann neu zählen.";

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Kassensturz", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = $"Soll-Bestand (errechnet): {GermanFormat.Eur(expectedCents)}\n" +
                           $"Ist-Bestand (gezählt): {GermanFormat.Eur(countedCents)}\n" +
                           $"Differenz: {GermanFormat.Eur(difference)}",
                    FontSize = 20
                },
                difference == 0
                    ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 15 }
                    : AppTheme.WarningBanner(text),
                note,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, recount, book }
                }
            }
        };
    }
}

public sealed class LicenseDeactivateConfirmWindow : Window
{
    public LicenseDeactivateConfirmWindow(string licenseId, string customerNumber)
    {
        Title = "Lizenz deaktivieren";
        Width = 620;
        Height = 360;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinWidth = 150,
            MinHeight = 52
        };
        cancel.Click += (_, _) => Close(false);

        var deactivate = new Button
        {
            Content = "LIZENZ DEAKTIVIEREN",
            MinWidth = 220,
            MinHeight = 52,
            Background = AppTheme.DangerRed,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };
        deactivate.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "Kommerzielle Lizenz deaktivieren",
                    FontSize = 24,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.OrangeRed
                },
                new TextBlock
                {
                    Text = $"Lizenz-ID: {licenseId}\nKunden-Nr.: {customerNumber}",
                    FontSize = 15
                },
                new TextBlock
                {
                    Text = "Nach der Deaktivierung wird der kommerzielle Verkauf auf diesem PC gesperrt. " +
                           "Dieselbe Lizenzdatei kann auf diesem PC nicht erneut aktiviert werden. " +
                           "Für eine erneute Aktivierung muss eine neue TOR-POS-Lizenz ausgestellt werden.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, deactivate }
                }
            }
        };
    }
}

public sealed record QuickItemResult(string Name, long PriceCents, decimal VatRate);

/// <summary>
/// R47: fast free-price item for kiosk/imbiss use. This creates only a current-cart line;
/// it does not create or mutate product master data and therefore carries a negative technical product id.
/// </summary>
public sealed class QuickItemWindow : Window
{
    private readonly TextBox _name = new() { Text = "Schnellartikel", MinWidth = 280 };
    private readonly TextBox _price = new() { PlaceholderText = "0,00", MinWidth = 180 };
    private readonly ComboBox _vat = new() { MinWidth = 180 };
    private readonly TextBlock _status = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };

    public QuickItemWindow()
    {
        Title = "Schnellartikel / freie Preiseingabe";
        Width = 560;
        Height = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _vat.ItemsSource = new[] { "19 %", "7 %" };
        _vat.SelectedIndex = 0;

        var ok = new Button
        {
            Content = "ZUM VERKAUF HINZUFÜGEN",
            MinHeight = 54,
            MinWidth = 240,
            FontWeight = FontWeight.Bold
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button { Content = "ABBRECHEN", MinHeight = 54, MinWidth = 140 };
        cancel.Click += (_, _) => Close((QuickItemResult?)null);

        _price.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept();
            }
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(26),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "SCHNELLARTIKEL", FontSize = 25, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = "Für Artikel ohne Stammdatensatz oder eine einmalige freie Preiseingabe. " +
                           "Der Schnellartikel wird nur in den aktuellen Bon gelegt; der Warenbestand wird nicht verändert.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72
                },
                Labeled("Bezeichnung", _name),
                Labeled("Preis €", _price),
                Labeled("MwSt.", _vat),
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, ok }
                }
            }
        };

        Opened += (_, _) =>
        {
            _price.Focus();
            _price.SelectAll();
        };
    }

    private static Control Labeled(string label, Control input)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*") };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private void Accept()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) name = "Schnellartikel";
        if (!Formatting.TryParseMoney(_price.Text, out var cents) || cents <= 0)
        {
            _status.Text = "Bitte einen Preis größer 0,00 € eingeben.";
            _price.Focus();
            _price.SelectAll();
            return;
        }

        var vat = _vat.SelectedIndex == 1 ? 7m : 19m;
        Close((QuickItemResult?)new QuickItemResult(name, cents, vat));
    }
}
