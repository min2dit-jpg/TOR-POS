using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class SumUpConnectionWindow : Window
{
    private readonly SumUpConnectionService _service = new();
    private readonly CancellationTokenSource _closing = new();

    public SumUpConnectionWindow()
    {
        Title = "SumUp Solo · Verbindung + 1,00 € Gerätetest"; Width = 780; Height = 860;
        MinWidth = 600; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var merchant = new TextBox { PlaceholderText = "Händlercode (Merchant Code)", MinHeight = 42 };
        var key = new TextBox { PlaceholderText = "SumUp API-Key", PasswordChar = '●', MinHeight = 42 };
        var code = new TextBox { PlaceholderText = "Kopplungscode vom Solo (nur bei neuer Kopplung)", MinHeight = 42 };
        var readers = new ComboBox { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Geräteliste laden, danach Solo auswählen" };
        var list = new Button { Content = "1 · GERÄTELISTE LADEN", MinHeight = 48 };
        var statusButton = new Button { Content = "2 · GERÄTESTATUS PRÜFEN", MinHeight = 48 };
        var sendOneEuro = new Button { Content = "3 · 1,00 € TEST AN SOLO SENDEN", MinHeight = 52 };
        var terminate = new Button { Content = "4 · TEST ABBRECHEN", MinHeight = 52 };
        var pair = new Button { Content = "SOLO MIT DIESEM KONTO KOPPELN", MinHeight = 48 };
        var status = new TextBlock
        {
            Text = "Verbindungstest. Der 1,00-€-Test sendet eine ECHTE Zahlungsanforderung an das Solo. KEINE KARTE vorhalten; danach ABBRECHEN.",
            TextWrapping = TextWrapping.Wrap
        };

        var warning = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "WICHTIG: 1,00 € SENDEN dient nur dazu zu prüfen, ob der Betrag auf dem Solo erscheint. Wenn eine Karte vorgehalten wird, kann eine echte Zahlung entstehen. Sobald 1,00 € am Solo sichtbar ist, TEST ABBRECHEN drücken oder direkt am Solo abbrechen.",
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap
            }
        };

        var inputs = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                merchant, key, list, readers, statusButton, warning, sendOneEuro, terminate,
                new TextBlock { Text = "Neues Gerät: Solo abmelden → Verbindungen / Connections → API → Verbinden / Connect. Code innerhalb von 5 Minuten verwenden.", TextWrapping = TextWrapping.Wrap },
                code, pair
            }
        };

        bool busy = false;
        async Task Run(Func<Task> operation, bool pairing = false)
        {
            if (busy) return;
            busy = true; inputs.IsEnabled = false; status.Text = "SumUp wird abgefragt ...";
            try { await operation(); }
            catch (OperationCanceledException) { status.Text = "Abgebrochen / Zeitlimit erreicht." + (pairing ? " Kopplung kann erfolgt sein: zuerst Geräteliste laden." : ""); }
            catch (HttpRequestException) { status.Text = "Netzwerkfehler. Internetverbindung prüfen." + (pairing ? " Kopplung kann erfolgt sein: zuerst Geräteliste laden." : ""); }
            catch (System.Text.Json.JsonException) { status.Text = "Unerwartete SumUp-Antwort. Geräteliste erneut prüfen."; }
            catch (Exception ex) { status.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "SumUp-Antwort konnte nicht verarbeitet werden. Geräteliste prüfen."; }
            finally { busy = false; inputs.IsEnabled = true; }
        }

        list.Click += async (_, _) => await Run(async () =>
        {
            readers.ItemsSource = null;
            var result = await _service.ListAsync(merchant.Text ?? "", key.Text ?? "", _closing.Token);
            readers.ItemsSource = result;
            if (result.Count > 0) readers.SelectedIndex = 0;
            status.Text = result.Count == 0
                ? "Zugang erfolgreich. Noch kein API-Reader gekoppelt. Unten einen Solo koppeln."
                : $"{result.Count} Gerät(e) gefunden. Gerät auswählen und Status prüfen. 'paired' allein bestätigt keine Online-Verbindung.";
        });

        statusButton.Click += async (_, _) => await Run(async () =>
        {
            if (readers.SelectedItem is not SumUpReader reader) throw new ArgumentException("Zuerst Geräteliste laden und Solo auswählen.");
            status.Text = await _service.StatusAsync(merchant.Text ?? "", key.Text ?? "", reader.Id, _closing.Token);
        });

        sendOneEuro.Click += async (_, _) => await Run(async () =>
        {
            if (readers.SelectedItem is not SumUpReader reader) throw new ArgumentException("Zuerst Geräteliste laden und Solo auswählen.");
            var checkout = await _service.StartOneEuroDeviceTestAsync(merchant.Text ?? "", key.Text ?? "", reader.Id, _closing.Token);
            status.Text = "1,00 € TESTANFORDERUNG wurde von der SumUp API angenommen.\n" +
                $"Checkout-ID: {checkout.CheckoutId}\n\n" +
                "JETZT SOLO ANSEHEN: Wenn dort 1,00 € erscheint, ist TOR POS → SumUp → Solo erfolgreich. " +
                "KEINE KARTE VORHALTEN. Danach sofort TEST ABBRECHEN drücken oder am Solo abbrechen.";
        });

        terminate.Click += async (_, _) => await Run(async () =>
        {
            if (readers.SelectedItem is not SumUpReader reader) throw new ArgumentException("Zuerst Geräteliste laden und Solo auswählen.");
            await _service.TerminateCheckoutAsync(merchant.Text ?? "", key.Text ?? "", reader.Id, _closing.Token);
            status.Text = "ABBRUCHANFORDERUNG an SumUp gesendet. SumUp liefert dafür keine synchrone Abbruchbestätigung. " +
                "Solo-Anzeige kontrollieren. Der Abbruch funktioniert nur, solange das Gerät auf eine Karten-/PIN-Aktion wartet.";
        });

        pair.Click += async (_, _) => await Run(async () =>
        {
            var reader = await _service.PairAsync(merchant.Text ?? "", key.Text ?? "", code.Text ?? "", _closing.Token);
            code.Text = ""; readers.ItemsSource = new[] { reader }; readers.SelectedIndex = 0;
            status.Text = $"Kopplungsantwort: {reader.PairingStatus} · {reader.Id}\nBestätigung am Solo kontrollieren; danach Geräteliste laden und Status prüfen. Keine Zahlung gestartet.";
        }, true);

        // Changing account credentials invalidates the displayed account's reader selection.
        merchant.TextChanged += (_, _) => readers.ItemsSource = null;
        key.TextChanged += (_, _) => readers.ItemsSource = null;

        var close = new Button { Content = "SCHLIESSEN", MinHeight = 48 };
        close.Click += (_, _) => Close();
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(22), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "SUMUP SOLO · VERBINDUNG + 1,00 € TEST", FontSize = 24 },
                    new TextBlock { Text = "KARTE TEST im normalen Kassenbild bleibt Simulation. Dieser separate Admin-Test sendet nur eine feste 1,00-€-Anforderung an das ausgewählte Solo; Zugangsdaten werden nicht gespeichert.", TextWrapping = TextWrapping.Wrap },
                    inputs, status, close
                }
            }
        };
        Closed += (_, _) => { _closing.Cancel(); key.Text = ""; code.Text = ""; _service.Dispose(); };
    }
}
