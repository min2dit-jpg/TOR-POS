using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class GooglePairingWindow : Window
{
    private readonly GoogleGmailService _google;
    private readonly GoogleGmailService.PairingSession _pairing;
    private readonly CancellationTokenSource _stop = new();
    private readonly TextBlock _status = new()
    {
        Text = "Warte auf Google-Anmeldung …",
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };

    public GooglePairingWindow(GoogleGmailService google, GoogleGmailService.PairingSession pairing)
    {
        _google = google;
        _pairing = pairing;
        Title = "TOR POS · Mit Google anmelden";
        Width = 560;
        Height = 690;
        MinWidth = 500;
        MinHeight = 640;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var qr = BuildQr(pairing.QrMatrix);
        var open = new Button
        {
            Content = "LINK AUF DIESEM PC ÖFFNEN",
            MinHeight = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeight.SemiBold
        };
        open.Click += (_, _) => OpenLink();

        var cancel = new Button
        {
            Content = "ABBRECHEN",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        cancel.Click += (_, _) => Close(false);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock
                    {
                        Text = "MIT GOOGLE ANMELDEN",
                        FontSize = 26,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "QR-Code mit dem Handy scannen. Google-Passwort und 2-Faktor-Code werden ausschließlich bei Google eingegeben – niemals in TOR POS.",
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Opacity = 0.78,
                        MaxWidth = 470
                    },
                    qr,
                    new TextBlock
                    {
                        Text = "TOR POS fordert nur die Berechtigung „E-Mails senden“ (gmail.send). Der QR-Code ist einmalig und läuft nach wenigen Minuten ab.",
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Opacity = 0.72,
                        MaxWidth = 470
                    },
                    _status,
                    open,
                    cancel
                }
            }
        };

        Opened += async (_, _) => await PollAsync();
        Closed += (_, _) => _stop.Cancel();
    }

    private static Control BuildQr(IReadOnlyList<string> matrix)
    {
        const int quiet = 4;
        const double module = 6;
        var modules = matrix.Count;
        var size = (modules + quiet * 2) * module;
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        for (var y = 0; y < modules; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x] != '1') continue;
                var square = new Rectangle { Width = module, Height = module, Fill = Brushes.Black };
                Canvas.SetLeft(square, (x + quiet) * module);
                Canvas.SetTop(square, (y + quiet) * module);
                canvas.Children.Add(square);
            }
        }
        return new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas
        };
    }

    private void OpenLink()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_pairing.DisplayUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = "Browser konnte nicht geöffnet werden: " + ex.Message;
        }
    }

    private async Task PollAsync()
    {
        while (!_stop.IsCancellationRequested && DateTimeOffset.UtcNow < _pairing.ExpiresAt.ToUniversalTime())
        {
            try
            {
                var result = await _google.CheckPairingAsync(_pairing, _stop.Token);
                if (result.Status == "COMPLETE")
                {
                    _status.Text = $"Google verbunden ✓\n{result.AccountEmail}";
                    await Task.Delay(850, _stop.Token);
                    if (!_stop.IsCancellationRequested) Close(true);
                    return;
                }
                if (result.Status == "ERROR")
                {
                    _status.Text = "Google-Anmeldung fehlgeschlagen:\n" + result.Error;
                    return;
                }
                _status.Text = "Warte auf Bestätigung am Handy …";
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _status.Text = "Verbindung wird erneut geprüft …\n" + ex.Message;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
        if (!_stop.IsCancellationRequested)
            _status.Text = "QR-Code abgelaufen. Fenster schließen und einen neuen QR-Code erzeugen.";
    }
}
