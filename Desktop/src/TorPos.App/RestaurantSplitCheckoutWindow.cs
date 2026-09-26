using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// German-only Restaurant item split flow. It prepares a RestaurantCheckoutDraft
/// for the existing payment-reservation path; it never commits a sale itself.
/// </summary>
public sealed class RestaurantSplitCheckoutWindow : Window
{
    private readonly RestaurantRepository _restaurant;
    private readonly RestaurantTableSession _session;
    private readonly IReadOnlyList<RestaurantSessionItem> _items;
    private readonly Dictionary<long, NumericUpDown> _quantities = new();

    private readonly TextBlock _summary = new()
    {
        FontSize = 18,
        FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.78
    };

    private readonly Button _checkout = new()
    {
        Content = "TEILRECHNUNG KASSIEREN",
        MinHeight = 50,
        MinWidth = 230,
        FontWeight = FontWeight.Bold,
        IsEnabled = false
    };

    public RestaurantSplitCheckoutWindow(
        RestaurantRepository restaurant,
        RestaurantTableSession session,
        IReadOnlyList<RestaurantSessionItem> items,
        string tableName)
    {
        _restaurant = restaurant;
        _session = session;
        _items = items
            .Where(x => x.State == RestaurantSessionItemState.Active)
            .OrderBy(x => x.Id)
            .ToArray();

        Title = "TOR Restaurant · Teilrechnung";
        Width = 760;
        Height = 720;
        MinWidth = 680;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));

        var rows = new StackPanel
        {
            Spacing = 8
        };

        foreach (var item in _items)
            rows.Children.Add(CreateItemRow(item));

        var all = new Button
        {
            Content = "ALLE POSITIONEN",
            MinHeight = 42,
            MinWidth = 155
        };
        all.Click += (_, _) =>
        {
            foreach (var item in _items)
                _quantities[item.Id].Value = item.Quantity;

            RefreshPreview();
        };

        var clear = new Button
        {
            Content = "AUSWAHL LEEREN",
            MinHeight = 42,
            MinWidth = 155
        };
        clear.Click += (_, _) =>
        {
            foreach (var input in _quantities.Values)
                input.Value = 0m;

            RefreshPreview();
        };

        // R-4 (variant B): split by persons with WHOLE positions. The plan only
        // fills the quantities above; payment goes through the same item split.
        var persons = new NumericUpDown
        {
            Minimum = 2,
            Maximum = RestaurantSplitPlanner.MaxPersons,
            Value = 2,
            Increment = 1,
            FormatString = "0",
            Width = 110,
            MinHeight = 42
        };
        var nextPerson = new Button
        {
            Content = "NÄCHSTE PERSON AUSWÄHLEN",
            MinHeight = 42,
            MinWidth = 230
        };
        nextPerson.Click += (_, _) => SelectNextPerson((int)(persons.Value ?? 2m));

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 50,
            MinWidth = 140
        };
        cancel.Click += (_, _) => Close(null);

        _checkout.Click += async (_, _) => await CheckoutAsync();

        var header = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "TEILRECHNUNG NACH POSITIONEN",
                    FontSize = 24,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = $"{tableName} · offene Positionen auswählen und bei Bedarf die Menge reduzieren.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78
                },
                new TextBlock
                {
                    Text = "Die Auswahl wird erst beim anschließenden Kassieren reserviert. Nicht ausgewählte Mengen bleiben am Tisch offen.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72
                }
            }
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(20),
            Children =
            {
                header,
                new ScrollViewer
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 16, 0, 16),
                    VerticalScrollBarVisibility =
                        Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = rows
                },
                new Border
                {
                    [Grid.RowProperty] = 2,
                    Padding = new Thickness(14),
                    Background = new SolidColorBrush(Color.Parse("#101925")),
                    CornerRadius = new CornerRadius(10),
                    Child = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            _summary,
                            _status,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { all, clear }
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "Verbleibende Personen:",
                                        VerticalAlignment = VerticalAlignment.Center
                                    },
                                    persons,
                                    nextPerson
                                }
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Children = { cancel, _checkout }
                            }
                        }
                    }
                }
            }
        };

        RefreshPreview();
    }

    private Control CreateItemRow(RestaurantSessionItem item)
    {
        var quantity = new NumericUpDown
        {
            Minimum = 0m,
            Maximum = item.Quantity,
            Value = 0m,
            Increment = string.Equals(item.Unit, "Stück", StringComparison.OrdinalIgnoreCase) ? 1m : 0.001m,
            // R-9.1: whole-piece positions show and accept whole pieces only;
            // the calculator refuses fractions of them anyway.
            FormatString = string.Equals(item.Unit, "Stück", StringComparison.OrdinalIgnoreCase) ? "0" : "0.###",
            Width = 110,
            MinHeight = 42
        };
        quantity.ValueChanged += (_, _) => RefreshPreview();
        _quantities[item.Id] = quantity;

        var full = new Button
        {
            Content = "GANZ",
            MinWidth = 75,
            MinHeight = 42
        };
        full.Click += (_, _) =>
        {
            quantity.Value = item.Quantity;
            RefreshPreview();
        };

        return new Border
        {
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.Parse("#101925")),
            BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = item.ProductName,
                                FontSize = 17,
                                FontWeight = FontWeight.Bold,
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text =
                                    $"Offen: {item.Quantity:0.###} × {Formatting.Money(item.UnitPriceCents)} · " +
                                    $"Summe {Formatting.Money(item.LineTotalCents)}",
                                Opacity = 0.72,
                                TextWrapping = TextWrapping.Wrap
                            }
                        }
                    },
                    new Border
                    {
                        [Grid.ColumnProperty] = 1,
                        Margin = new Thickness(10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = quantity
                    },
                    new Border
                    {
                        [Grid.ColumnProperty] = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = full
                    }
                }
            }
        };
    }

    /// <summary>
    /// R-4: selects the share of the first of the remaining persons. After that
    /// person has paid, the window is opened again with one person fewer and the
    /// rest is distributed anew, so the shares stay as even as whole positions allow.
    /// </summary>
    private void SelectNextPerson(int persons)
    {
        try
        {
            var plan = RestaurantSplitPlanner.DistributeWholeItems(_items, persons);
            foreach (var input in _quantities.Values)
                input.Value = 0m;
            foreach (var selection in plan[0])
                _quantities[selection.SessionItemId].Value = selection.QuantityMilli / 1000m;

            RefreshPreview();

            var shares = plan
                .Select((share, index) => share.Length == 0
                    ? 0L
                    : RestaurantSplitCalculator.ByItems(_items, share).TotalCents)
                .Select((cents, index) => $"P{index + 1}: {Formatting.Money(cents)}");
            _status.Text =
                $"Aufteilung auf {persons} Personen nach ganzen Positionen · " +
                string.Join(" · ", shares) +
                ". Nach dem Kassieren Teilrechnung erneut öffnen und mit einer Person weniger fortfahren.";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private RestaurantSplitSelection[] CurrentSelections()
    {
        return _items
            .Select(item =>
            {
                var value = _quantities[item.Id].Value ?? 0m;
                var milli = (long)Math.Round(
                    value * 1000m,
                    MidpointRounding.AwayFromZero);

                milli = Math.Clamp(
                    milli,
                    0,
                    item.QuantityMilli);

                return new RestaurantSplitSelection(
                    item.Id,
                    milli);
            })
            .Where(x => x.QuantityMilli > 0)
            .ToArray();
    }

    private void RefreshPreview()
    {
        try
        {
            var selections = CurrentSelections();
            if (selections.Length == 0)
            {
                _summary.Text = "Ausgewählt: 0,00 €";
                _status.Text =
                    "Mindestens eine Position oder Teilmenge auswählen.";
                _checkout.IsEnabled = false;
                return;
            }

            var quote = RestaurantSplitCalculator.ByItems(
                _items,
                selections);

            _summary.Text =
                $"Ausgewählt: {Formatting.Money(quote.TotalCents)} · " +
                $"{quote.Lines.Count} Position(en)";

            _status.Text = string.Join(
                " · ",
                quote.Lines.Select(x =>
                    $"{x.Quantity:0.###} × {x.ProductName}"));

            _checkout.IsEnabled =
                !quote.IsEmpty &&
                quote.TotalCents > 0;
        }
        catch (Exception ex)
        {
            _summary.Text = "Auswahl prüfen";
            _status.Text = ex.Message;
            _checkout.IsEnabled = false;
        }
    }

    private async Task CheckoutAsync()
    {
        if (!_checkout.IsEnabled)
            return;

        _checkout.IsEnabled = false;

        try
        {
            var selections = CurrentSelections();
            if (selections.Length == 0)
            {
                RefreshPreview();
                return;
            }

            var draft = await _restaurant.BuildCheckoutDraftAsync(
                _session.Id,
                _session.Version,
                selections);

            Close(draft);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _checkout.IsEnabled = true;
        }
    }
}
