using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class PromotionManagementWindow : Window
{
    private readonly PromotionCampaignService _service;
    private readonly AuthenticatedUser _user;

    private readonly long? _categoryId;
    private readonly string _categoryName;
    private readonly long? _productId;
    private readonly string _productName;

    private readonly ListBox _campaignList = new()
    {
        Height = 510
    };
    private readonly TextBox _name = new()
    {
        PlaceholderText = "z. B. DÖNER ANGEBOT"
    };
    private readonly ComboBox _scope = new();
    private readonly DatePicker _start = new();
    private readonly DatePicker _end = new();
    private readonly TextBlock _percentText = new()
    {
        Text = "20 %",
        FontSize = 26,
        FontWeight = FontWeight.Bold,
        Foreground = AppTheme.AccentTeal
    };
    private readonly TextBlock _context = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.TextMuted
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.WarningAmber
    };

    private IReadOnlyList<PromotionCampaign> _campaigns =
        Array.Empty<PromotionCampaign>();

    private int _percent = 20;

    public PromotionManagementWindow(
        PromotionCampaignService service,
        AuthenticatedUser user,
        PromotionScope defaultScope = PromotionScope.All,
        long? categoryId = null,
        string categoryName = "",
        long? productId = null,
        string productName = "")
    {
        _service = service;
        _user = user;
        _categoryId = categoryId;
        _categoryName = categoryName ?? "";
        _productId = productId;
        _productName = productName ?? "";

        Title = "TOR POS · ANGEBOTE / AKTIONEN";
        Width = 1080;
        Height = 760;
        MinWidth = 900;
        MinHeight = 650;
        WindowStartupLocation =
            WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        _scope.ItemsSource = new[]
        {
            "ALLE ARTIKEL",
            "AKTUELLE WARENGRUPPE",
            "AKTUELLER ARTIKEL"
        };

        _scope.SelectedIndex = defaultScope switch
        {
            PromotionScope.Category => 1,
            PromotionScope.Product => 2,
            _ => 0
        };

        var today = DateTimeOffset.Now;
        _start.SelectedDate = today;
        _end.SelectedDate = today.AddDays(7);

        _scope.SelectionChanged +=
            (_, _) => RefreshContext();

        var percentButtons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8
        };

        foreach (var percent in new[]
                 {
                     10, 15, 20, 25, 30, 40, 50
                 })
        {
            var button = new Button
            {
                Content = $"{percent} %",
                MinWidth = 88,
                MinHeight = 52,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Tag = percent
            };

            button.Click += (_, _) =>
            {
                _percent = (int)button.Tag!;
                _percentText.Text = $"{_percent} %";
            };

            percentButtons.Children.Add(button);
        }

        var create = new Button
        {
            Content = "ANGEBOT AKTIVIEREN",
            MinWidth = 220,
            MinHeight = 54,
            FontWeight = FontWeight.Bold,
            Background = new SolidColorBrush(Color.Parse("#0F7A55")),
            BorderBrush = AppTheme.AccentTeal
        };

        create.Click += async (_, _) =>
            await CreateAsync();

        var disable = new Button
        {
            Content = "AUSGEWÄHLTES ANGEBOT DEAKTIVIEREN",
            MinWidth = 280,
            MinHeight = 50,
            Background = AppTheme.DangerRed,
            BorderBrush = new SolidColorBrush(Color.Parse("#FF8F9D"))
        };

        disable.Click += async (_, _) =>
            await DisableAsync();

        var refresh = new Button
        {
            Content = "AKTUALISIEREN",
            MinWidth = 150,
            MinHeight = 50
        };

        refresh.Click += async (_, _) =>
            await ReloadAsync();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinWidth = 150,
            MinHeight = 50
        };

        close.Click += (_, _) => Close();

        var form = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                TitleText("NEUES ANGEBOT"),
                Hint(
                    "Normalpreise werden nicht überschrieben. " +
                    "Das Angebot wird erst beim Verkauf berechnet und " +
                    "als unveränderbarer Snapshot im Bon gespeichert."),
                Field("Name", _name),
                Label("Rabatt"),
                percentButtons,
                _percentText,
                Field("Gültig von", _start),
                Field("Gültig bis einschließlich", _end),
                Field("Anwenden auf", _scope),
                _context,
                new Border
                {
                    Background = new SolidColorBrush(
                        Color.Parse("#302713")),
                    BorderBrush = new SolidColorBrush(
                        Color.Parse("#7A6222")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = new TextBlock
                    {
                        Text =
                            "Pfand wird nicht rabattiert. " +
                            "Bei überschneidenden Angeboten gewinnt der höchste Prozentsatz; " +
                            "bei Gleichstand Artikel > Warengruppe > Alle Artikel.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = AppTheme.WarningAmber
                    }
                },
                create,
                _status
            }
        };

        var left = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                TitleText("ANGEBOTE / AKTIONEN"),
                Hint(
                    "AKTIV, GEPLANT, ABGELAUFEN und DEAKTIVIERT bleiben " +
                    "zur Nachvollziehbarkeit sichtbar."),
                _campaignList,
                new WrapPanel
                {
                    ItemSpacing = 8,
                    LineSpacing = 8,
                    Children =
                    {
                        refresh,
                        disable
                    }
                }
            }
        };

        Content = new Grid
        {
            Margin = new Thickness(22),
            ColumnDefinitions =
                new ColumnDefinitions("1.1*,0.9*"),
            ColumnSpacing = 18,
            Children =
            {
                new Border
                {
                    Padding = new Thickness(16),
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(
                        Color.Parse("#111F30")),
                    BorderBrush = new SolidColorBrush(
                        Color.Parse("#29445D")),
                    BorderThickness = new Thickness(1),
                    Child = left
                },
                new Border
                {
                    [Grid.ColumnProperty] = 1,
                    Padding = new Thickness(16),
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(
                        Color.Parse("#111F30")),
                    BorderBrush = new SolidColorBrush(
                        Color.Parse("#29445D")),
                    BorderThickness = new Thickness(1),
                    Child = new ScrollViewer
                    {
                        Content = form
                    }
                }
            }
        };

        Opened += async (_, _) =>
        {
            UiLanguage.Apply(this);
            RefreshContext();
            await ReloadAsync();
        };
    }

    private async Task ReloadAsync()
    {
        try
        {
            _campaigns =
                await _service.GetAllAsync();

            var today =
                DateOnly.FromDateTime(
                    DateTime.Now);

            _campaignList.ItemsSource =
                _campaigns.Select(x =>
                    $"{x.StatusFor(today),-11} · " +
                    $"{x.DiscountPercent,2}% · " +
                    $"{x.Name} · {x.TargetName} · " +
                    $"{x.StartDate:dd.MM.yyyy}–{x.EndDate:dd.MM.yyyy}")
                .ToArray();

            _status.Text =
                $"{_campaigns.Count} Angebot(e) geladen.";
        }
        catch (Exception ex)
        {
            _status.Text =
                "FEHLER: " + ex.Message;
        }
    }

    private async Task CreateAsync()
    {
        try
        {
            if (_start.SelectedDate is null ||
                _end.SelectedDate is null)
            {
                _status.Text =
                    "Start- und Enddatum sind erforderlich.";
                return;
            }

            var start =
                DateOnly.FromDateTime(
                    _start.SelectedDate.Value.DateTime);

            var end =
                DateOnly.FromDateTime(
                    _end.SelectedDate.Value.DateTime);

            var (scope, targetId, targetName) =
                ResolveScope();

            if (scope != PromotionScope.All &&
                targetId <= 0)
            {
                _status.Text =
                    scope == PromotionScope.Category
                        ? "Bitte das Fenster aus einer ausgewählten Warengruppe öffnen."
                        : "Bitte zuerst einen Artikel auswählen.";
                return;
            }

            var id =
                await _service.CreateAsync(
                    new PromotionCreateRequest(
                        Name: (_name.Text ?? "").Trim(),
                        DiscountPercent: _percent,
                        StartDate: start,
                        EndDate: end,
                        Scope: scope,
                        TargetId: targetId,
                        TargetName: targetName),
                    _user.Username);

            _status.Text =
                $"ANGEBOT #{id} gespeichert · {_percent}% · " +
                $"{start:dd.MM.yyyy}–{end:dd.MM.yyyy}";

            _name.Text = "";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _status.Text =
                "FEHLER: " + ex.Message;
        }
    }

    private async Task DisableAsync()
    {
        var index = _campaignList.SelectedIndex;

        if (index < 0 ||
            index >= _campaigns.Count)
        {
            _status.Text =
                "Bitte zuerst ein Angebot auswählen.";
            return;
        }

        var campaign = _campaigns[index];

        if (!campaign.IsEnabled)
        {
            _status.Text =
                "Dieses Angebot ist bereits deaktiviert.";
            return;
        }

        var reason =
            await new PosActionReasonWindow(
                "ANGEBOT DEAKTIVIEREN",
                $"{campaign.Name} · {campaign.DiscountPercent}% · " +
                $"{campaign.TargetName} · " +
                $"{campaign.StartDate:dd.MM.yyyy}–{campaign.EndDate:dd.MM.yyyy}",
                new[]
                {
                    "Aktion beendet",
                    "Falscher Zeitraum",
                    "Falscher Rabatt",
                    "Falscher Bereich",
                    "Sonstiger Grund"
                })
                .ShowDialog<string?>(this);

        if (string.IsNullOrWhiteSpace(reason))
            return;

        try
        {
            await _service.DisableAsync(
                campaign.Id,
                _user.Username,
                reason);

            _status.Text =
                $"Angebot #{campaign.Id} deaktiviert.";

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _status.Text =
                "FEHLER: " + ex.Message;
        }
    }

    private (PromotionScope Scope, long TargetId, string TargetName)
        ResolveScope()
    {
        return _scope.SelectedIndex switch
        {
            1 => (
                PromotionScope.Category,
                _categoryId ?? 0,
                _categoryName),

            2 => (
                PromotionScope.Product,
                _productId ?? 0,
                _productName),

            _ => (
                PromotionScope.All,
                0,
                "Alle Artikel")
        };
    }

    private void RefreshContext()
    {
        var resolved = ResolveScope();

        _context.Text = resolved.Scope switch
        {
            PromotionScope.Category =>
                resolved.TargetId > 0
                    ? $"Warengruppe: {resolved.TargetName}"
                    : "Warengruppe: keine ausgewählt",

            PromotionScope.Product =>
                resolved.TargetId > 0
                    ? $"Artikel: {resolved.TargetName}"
                    : "Artikel: keiner ausgewählt",

            _ => "Alle aktiven Artikel"
        };
    }

    private static TextBlock TitleText(string text) =>
        new()
        {
            Text = text,
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };

    private static TextBlock Hint(string text) =>
        new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(
                Color.Parse("#9DB4C9"))
        };

    private static TextBlock Label(string text) =>
        new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(
                Color.Parse("#D7E6F4"))
        };

    private static Control Field(
        string label,
        Control control) =>
        new StackPanel
        {
            Spacing = 5,
            Children =
            {
                Label(label),
                control
            }
        };
}
