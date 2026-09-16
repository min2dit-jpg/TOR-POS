using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TorPos.App;

public sealed class PosActionReasonWindow : Window
{
    private readonly ComboBox _reason;
    private readonly TextBox _note;
    private readonly TextBlock _validation;

    public PosActionReasonWindow(
        string actionTitle,
        string summary,
        IReadOnlyList<string> reasons)
    {
        Title = actionTitle + " · Grund";
        Width = 620;
        Height = 470;
        MinWidth = 560;
        MinHeight = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var normalized = reasons
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _reason = new ComboBox
        {
            ItemsSource = normalized,
            MinHeight = 46,
            PlaceholderText = "Grund auswählen"
        };

        if (normalized.Length > 0)
            _reason.SelectedIndex = 0;

        _note = new TextBox
        {
            MinHeight = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Optionaler Zusatz / Notiz"
        };

        _validation = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.Parse("#FF9BA6")),
            TextWrapping = TextWrapping.Wrap
        };

        var confirm = new Button
        {
            Content = "BESTÄTIGEN",
            MinHeight = 54,
            MinWidth = 180,
            FontWeight = FontWeight.Bold,
            Background = new SolidColorBrush(Color.Parse("#8E3038")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF8F9D"))
        };

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 54,
            MinWidth = 160
        };

        confirm.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close((string?)null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = actionTitle.ToUpperInvariant(),
                    FontSize = 25,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.Parse("#14263A")),
                    Child = new TextBlock
                    {
                        Text = summary,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#D7E6F4"))
                    }
                },
                new TextBlock
                {
                    Text = "Grund ist Pflicht und wird unveränderbar protokolliert.",
                    Foreground = AppTheme.WarningAmber
                },
                _reason,
                _note,
                _validation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, confirm }
                }
            }
        };

        Opened += (_, _) => UiLanguage.Apply(this);
    }

    private void Confirm()
    {
        var reason = _reason.SelectedItem?.ToString()?.Trim() ?? "";
        var note = (_note.Text ?? "").Trim();

        if (reason.Length == 0)
        {
            _validation.Text = "Bitte einen Grund auswählen.";
            return;
        }

        var result = note.Length == 0
            ? reason
            : reason + " · " + note;

        Close((string?)result);
    }
}
