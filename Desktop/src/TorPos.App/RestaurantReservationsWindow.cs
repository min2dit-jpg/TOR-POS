using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class RestaurantReservationsWindow : Window
{
    private readonly RestaurantReservationService _reservations;
    private readonly RestaurantRepository _restaurant;
    private readonly AuthenticatedUser _user;

    private readonly TextBox _dateTime = new()
    {
        MinHeight = 42,
        PlaceholderText = "z. B. 24.09.2026 19:30"
    };

    private readonly NumericUpDown _guests = new()
    {
        Minimum = 1,
        Maximum = 999,
        Value = 2,
        MinHeight = 42
    };

    private readonly TextBox _name = new()
    {
        MinHeight = 42,
        MaxLength = 160
    };

    private readonly TextBox _phone = new()
    {
        MinHeight = 42,
        MaxLength = 80
    };

    private readonly TextBox _note = new()
    {
        MinHeight = 72,
        MaxLength = 1000,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap
    };

    private readonly ComboBox _table = new()
    {
        MinHeight = 42,
        PlaceholderText = "Kein Tisch fest zugeordnet"
    };

    private readonly ListBox _list = new()
    {
        MinHeight = 420
    };

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.78
    };

    private IReadOnlyList<RestaurantTable> _tables = Array.Empty<RestaurantTable>();
    private RestaurantReservation? _selected;

    public RestaurantReservationsWindow(
        RestaurantReservationService reservations,
        RestaurantRepository restaurant,
        AuthenticatedUser user)
    {
        _reservations = reservations;
        _restaurant = restaurant;
        _user = user;

        Title = "TOR Restaurant Plus · Reservierungen";
        Width = 1180;
        Height = 760;
        MinWidth = 980;
        MinHeight = 640;
        Background = new SolidColorBrush(Color.Parse("#0D1420"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _table.ItemTemplate =
            new Avalonia.Controls.Templates.FuncDataTemplate<RestaurantTable>(
                (t, _) => new TextBlock
                {
                    Text = t?.DisplayName ?? ""
                });

        _list.ItemTemplate =
            new Avalonia.Controls.Templates.FuncDataTemplate<RestaurantReservation>(
                (r, _) => new TextBlock
                {
                    Text = r is null
                        ? ""
                        : $"{r.ReservationAt.ToLocalTime():dd.MM.yyyy HH:mm} · " +
                          $"{r.CustomerName} · {r.GuestCount} Pers. · " +
                          $"{TableName(r.TableId)} · {StatusText(r.Status)}",
                    Margin = new Thickness(6),
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap
                });

        _list.SelectionChanged += (_, _) =>
        {
            _selected = _list.SelectedItem as RestaurantReservation;
            RefreshSelectionStatus();
        };

        var create = new Button
        {
            Content = "RESERVIERUNG ANLEGEN",
            MinHeight = 46,
            FontWeight = FontWeight.Bold
        };
        create.Click += async (_, _) => await CreateAsync();

        var arrived = new Button
        {
            Content = "GEKOMMEN",
            MinHeight = 42
        };
        arrived.Click += async (_, _) => await SetSelectedStatusAsync("SEATED");

        var completed = new Button
        {
            Content = "ABGESCHLOSSEN",
            MinHeight = 42
        };
        completed.Click += async (_, _) => await SetSelectedStatusAsync("COMPLETED");

        var cancel = new Button
        {
            Content = "STORNO",
            MinHeight = 42
        };
        cancel.Click += async (_, _) => await SetSelectedStatusAsync("CANCELLED");

        var noShow = new Button
        {
            Content = "NICHT ERSCHIENEN",
            MinHeight = 42
        };
        noShow.Click += async (_, _) => await SetSelectedStatusAsync("NO_SHOW");

        var refresh = new Button
        {
            Content = "AKTUALISIEREN",
            MinHeight = 42
        };
        refresh.Click += async (_, _) => await ReloadAsync();

        var left = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                Header("NEUE RESERVIERUNG"),
                Label("Datum / Uhrzeit"),
                _dateTime,
                Label("Personen"),
                _guests,
                Label("Kundenname"),
                _name,
                Label("Telefon"),
                _phone,
                Label("Tisch (optional)"),
                _table,
                Label("Notiz"),
                _note,
                create
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { arrived, completed, cancel, noShow, refresh }
        };

        var right = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Header("RESERVIERUNGEN · NÄCHSTE 7 TAGE"),
                _list,
                actions,
                _status
            }
        };

        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0.85*,1.5*"),
            ColumnSpacing = 16,
            Margin = new Thickness(18),
            Children =
            {
                Card(left),
                new Border
                {
                    [Grid.ColumnProperty] = 1,
                    Background = new SolidColorBrush(Color.Parse("#101925")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(16),
                    Child = right
                }
            }
        };

        Opened += async (_, _) =>
        {
            var defaultAt = DateTime.Now.AddHours(2);
            _dateTime.Text = defaultAt.ToString("dd.MM.yyyy HH:mm");
            await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        _tables = await _restaurant.ListTablesAsync();
        _table.ItemsSource = _tables;

        var from = DateTimeOffset.Now.Date;
        var to = from.AddDays(7);
        var rows = await _reservations.ListAsync(from, to);

        _list.ItemsSource = rows;
        _selected = null;
        _status.Text = rows.Count == 0
            ? "Keine Reservierungen im ausgewählten Zeitraum."
            : $"{rows.Count} Reservierung(en) in den nächsten 7 Tagen.";
    }

    private async Task CreateAsync()
    {
        try
        {
            if (!DateTime.TryParseExact(
                    (_dateTime.Text ?? "").Trim(),
                    "dd.MM.yyyy HH:mm",
                    CultureInfo.GetCultureInfo("de-DE"),
                    DateTimeStyles.None,
                    out var local))
            {
                throw new InvalidOperationException(
                    "Datum/Uhrzeit bitte im Format TT.MM.JJJJ HH:MM eingeben.");
            }

            var guests = Math.Clamp(
                Convert.ToInt32(_guests.Value ?? 1m),
                1,
                999);

            var tableId = (_table.SelectedItem as RestaurantTable)?.Id;

            Task Create(bool extendTable) => _reservations.CreateAsync(
                new DateTimeOffset(local),
                120,
                guests,
                _name.Text ?? "",
                _phone.Text ?? "",
                _note.Text ?? "",
                tableId,
                _user.Username,
                extendTable: extendTable);

            try
            {
                await Create(extendTable: false);
            }
            catch (RestaurantTableCapacityException capacity)
            {
                if (!await ConfirmExtendTableAsync(capacity))
                {
                    _status.Text = capacity.Message;
                    return;
                }
                await Create(extendTable: true);
            }

            _name.Text = "";
            _phone.Text = "";
            _note.Text = "";
            _table.SelectedItem = null;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task<bool> ConfirmExtendTableAsync(RestaurantTableCapacityException capacity)
    {
        var dialog = new Window
        {
            Title = "TISCH ERWEITERN?",
            Width = 600,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = $"TISCH ERWEITERN (+{capacity.GuestCount - capacity.Seats} PLÄTZE)", MinWidth = 260, MinHeight = 48 };
        var no = new Button { Content = "ABBRECHEN", MinWidth = 150, MinHeight = 48 };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "TISCH ERWEITERN?", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock { Text = capacity.Message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { yes, no } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task SetSelectedStatusAsync(string status)
    {
        if (_selected is null)
        {
            _status.Text = "Bitte zuerst eine Reservierung auswählen.";
            return;
        }

        try
        {
            await _reservations.SetStatusAsync(
                _selected.Id,
                _selected.Version,
                status,
                _user.Username);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            await ReloadAsync();
        }
    }

    private void RefreshSelectionStatus()
    {
        if (_selected is null)
            return;

        _status.Text =
            $"{_selected.CustomerName} · " +
            $"{_selected.ReservationAt.ToLocalTime():dd.MM.yyyy HH:mm} · " +
            $"{StatusText(_selected.Status)}";
    }

    private string TableName(long? tableId) =>
        tableId is long id
            ? _tables.FirstOrDefault(x => x.Id == id)?.DisplayName
                ?? $"Tisch {id}"
            : "ohne Tisch";

    private static string StatusText(string status) =>
        status switch
        {
            "BOOKED" => "RESERVIERT",
            "SEATED" => "GEKOMMEN",
            "CANCELLED" => "STORNIERT",
            "NO_SHOW" => "NICHT ERSCHIENEN",
            "COMPLETED" => "ABGESCHLOSSEN",
            _ => status
        };

    private static TextBlock Header(string text) =>
        new()
        {
            Text = text,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#53E0C0"))
        };

    private static TextBlock Label(string text) =>
        new()
        {
            Text = text,
            FontWeight = FontWeight.Bold
        };

    private static Border Card(Control child) =>
        new()
        {
            Background = new SolidColorBrush(Color.Parse("#101925")),
            BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = child
        };
}
