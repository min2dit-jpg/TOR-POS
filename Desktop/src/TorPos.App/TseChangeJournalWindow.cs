using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// Shows the append-only TSE-Wechsel journal and lets an administrator record
/// the state of the (manual) notification to the tax office, e.g. after a
/// Mitteilung in Mein ELSTER with its Transferticket. Nothing here changes a
/// TSE, a sale or a DSFinV-K record, and nothing is sent anywhere.
/// </summary>
public sealed class TseChangeJournalWindow : Window
{
    private readonly ITseChangeJournal _journal;
    private readonly string _actor;
    private readonly bool _canEdit;
    private readonly ListBox _list = new() { MinHeight = 260 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#D7E6F4")) };
    private readonly ComboBox _status = new() { MinHeight = 44, MinWidth = 240 };
    private readonly TextBox _reference = new() { MinHeight = 44, PlaceholderText = "Referenz (z. B. ELSTER-Transferticket)" };
    private readonly TextBox _note = new() { MinHeight = 44, PlaceholderText = "Notiz" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.WarningAmber };
    private IReadOnlyList<TseChangeRecord> _changes = Array.Empty<TseChangeRecord>();

    public TseChangeJournalWindow(ITseChangeJournal journal, string actor, bool canEdit)
    {
        _journal = journal;
        _actor = actor;
        _canEdit = canEdit;

        Title = "TSE-Wechselprotokoll";
        Width = 980;
        Height = 720;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        _status.ItemsSource = Enum.GetValues<TseChangeNotificationStatus>();
        _list.SelectionChanged += async (_, _) => await ShowSelectedAsync();

        var save = new Button
        {
            Content = "MELDESTATUS SPEICHERN",
            MinHeight = 48,
            MinWidth = 220,
            FontWeight = FontWeight.Bold,
            IsEnabled = canEdit
        };
        save.Click += async (_, _) => await SaveStatusAsync();

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 48, MinWidth = 160 };
        close.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "TSE-WECHSELPROTOKOLL", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    new TextBlock
                    {
                        Text = "Jeder TSE-Wechsel wird unveränderbar protokolliert. Frühere Vorgänge bleiben ihrer TSE zugeordnet. " +
                               "Eine automatische Meldung an das Finanzamt ist nicht freigegeben; die Mitteilung nach § 146a Abs. 4 AO erfolgt über Mein ELSTER.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#D7E6F4"))
                    },
                    _list,
                    new Border
                    {
                        Padding = new Thickness(12),
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.Parse("#14263A")),
                        Child = _details
                    },
                    new TextBlock { Text = "Meldestatus", FontWeight = FontWeight.Bold, Foreground = Brushes.White },
                    _status,
                    _reference,
                    _note,
                    _message,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { close, save }
                    }
                }
            }
        };

        Opened += async (_, _) =>
        {
            UiLanguage.Apply(this);
            await ReloadAsync();
        };
    }

    public static TseChangeJournalWindow ForDatabase(string actor, bool canEdit) =>
        new(new TseChangeJournal(new SqliteDatabase(AppPaths.DatabasePath)), actor, canEdit);

    private async Task ReloadAsync()
    {
        try
        {
            _changes = (await _journal.ListAsync()).Reverse().ToList();
            _list.ItemsSource = _changes.Select(Line).ToList();
            _message.Text = _changes.Count == 0 ? "Noch kein TSE-Wechsel protokolliert." : "";
            if (_changes.Count > 0)
                _list.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _message.Text = "TSE-Wechselprotokoll konnte nicht gelesen werden: " + ex.Message;
        }
    }

    private static string Line(TseChangeRecord x) =>
        $"{x.OccurredAt.ToLocalTime():dd.MM.yyyy HH:mm} · {(x.Outcome == TseChangeOutcome.Succeeded ? "OK" : "FEHLER")} · " +
        $"{Or(x.Previous.SerialNumber, "(keine)")} → {x.Next.SerialNumber} · {x.Reason} · {x.Actor}";

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private TseChangeRecord? Selected =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _changes.Count ? _changes[_list.SelectedIndex] : null;

    private async Task ShowSelectedAsync()
    {
        var change = Selected;
        if (change is null)
        {
            _details.Text = "";
            return;
        }

        var history = await _journal.NotificationHistoryAsync(change.ChangeId);
        var current = history.Count == 0 ? TseChangeNotificationStatus.NotRequired : history[^1].Status;
        _status.SelectedItem = current;
        _details.Text =
            $"Zeitpunkt: {change.OccurredAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}\n" +
            $"Ergebnis: {change.Outcome}{(change.ErrorMessage.Length > 0 ? $" ({change.ErrorCode} {change.ErrorMessage})" : "")}\n" +
            $"Alte TSE: {Or(change.Previous.SerialNumber, "(keine)")} · {change.PreviousKind} · {Or(change.Previous.ProviderId, "-")} · Zertifikat bis {Or(change.Previous.CertificateValidUntil, "-")}\n" +
            $"Neue TSE: {change.Next.SerialNumber} · {change.NextKind} · {Or(change.Next.ProviderId, "-")} · Zertifikat bis {Or(change.Next.CertificateValidUntil, "-")} · BSI {Or(change.Next.BsiCertificationId, "-")}\n" +
            $"Grund: {change.Reason} · Benutzer: {change.Actor}\n" +
            $"Kasse: {change.KassenId} · Terminal: {change.TerminalId} · Client-ID: {Or(change.ClientId, "-")} · Mandant: {Or(change.Mandant, "-")}\n" +
            "Meldestatus-Verlauf:\n" +
            string.Join("\n", history.Select(h =>
                $"  {h.At.ToLocalTime():dd.MM.yyyy HH:mm} {h.Status} · {h.Actor}" +
                (h.SubmissionReference.Length > 0 ? $" · Ref. {h.SubmissionReference}" : "") +
                (h.Note.Length > 0 ? $" · {h.Note}" : "")));
    }

    private async Task SaveStatusAsync()
    {
        var change = Selected;
        if (!_canEdit || change is null || _status.SelectedItem is not TseChangeNotificationStatus status)
            return;
        try
        {
            await _journal.SetNotificationStatusAsync(change.ChangeId, status, _actor, (_note.Text ?? "").Trim(), (_reference.Text ?? "").Trim());
            _note.Text = "";
            _reference.Text = "";
            _message.Text = "Meldestatus gespeichert.";
            await ShowSelectedAsync();
        }
        catch (Exception ex)
        {
            _message.Text = "⚠ " + ex.Message;
        }
    }
}
