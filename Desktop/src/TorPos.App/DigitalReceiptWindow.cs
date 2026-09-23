using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TorPos.Core;

namespace TorPos.App;

public enum ReceiptChoice
{
    Papierbeleg,
    Digitalbeleg
}

/// <summary>
/// R145: after the sale the customer chooses how to receive the receipt.
/// AEAO zu § 146a Nr. 2.5.3: an electronic receipt needs the customer's consent,
/// which needs no particular form - choosing "Digitalbeleg" here is that
/// consent. Enter, Escape and closing the window mean paper, the receipt that
/// needs no consent.
/// </summary>
public sealed class ReceiptChoiceWindow : Window
{
    public ReceiptChoiceWindow(long totalCents, bool testReceipt)
    {
        Opened += (_, _) => UiLanguage.Apply(this);
        Title = "Beleg";
        Width = 640;
        Height = 400;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var paper = new Button
        {
            Content = "PAPIERBELEG",
            MinWidth = 260,
            MinHeight = 96,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsDefault = true
        };
        paper.Click += (_, _) => Close(ReceiptChoice.Papierbeleg);

        var digital = new Button
        {
            Content = "DIGITALBELEG (QR)",
            MinWidth = 260,
            MinHeight = 96,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Background = AppTheme.InfoBlue,
            BorderBrush = AppTheme.InfoBlueBorder,
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        digital.Click += (_, _) => Close(ReceiptChoice.Digitalbeleg);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(ReceiptChoice.Papierbeleg);
            }
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = testReceipt ? "BELEG · TESTBETRIEB" : "BELEG", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock { Text = Formatting.Money(totalCents), FontSize = 34, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = "Wie möchte der Kunde den Beleg erhalten?",
                    FontSize = 18,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 16,
                    Children = { paper, digital }
                },
                new TextBlock
                {
                    Text = "Digitalbeleg: Der Kunde scannt den QR-Code und öffnet den Bon im Browser - lesen, als PDF herunterladen, teilen oder drucken. Nur mit seiner Zustimmung.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = AppTheme.TextMuted
                }
            }
        };

        Opened += (_, _) => paper.Focus();
    }
}

/// <summary>
/// R103, R145: the QR code of the digital receipt on the cashier's screen (the
/// Kundendisplay shows it too, R104). Since R145 the link points at TOR Cloud,
/// so the customer can open the receipt anywhere, not only in the shop's WiFi.
/// </summary>
public sealed class DigitalReceiptWindow : Window
{
    private readonly TextBlock _heading = new()
    {
        Text = "DIGITALER KASSENBON",
        FontSize = 22,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private readonly TextBlock _message = new()
    {
        Text = "Wird bei TOR Cloud erstellt …",
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 15
    };

    private readonly Image _image = new() { Width = 280, Height = 280, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };

    private readonly TextBlock _link = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 11,
        Opacity = 0.55,
        IsVisible = false
    };

    public DigitalReceiptWindow()
    {
        Title = "Digitaler Kassenbon";
        Width = 440;
        Height = 620;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = "FERTIG",
            MinHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.Bold,
            FontSize = 18
        };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children = { _heading, _message, _image, _link, close }
        };

        Opened += (_, _) => UiLanguage.Apply(this);
    }

    public void ShowLink(string url, DateTimeOffset expiresAt)
    {
        _heading.Text = "IHR DIGITALER KASSENBON";
        _message.Text = UiLanguage.T("Mit dem Handy scannen: Bon ansehen, als PDF herunterladen, teilen oder drucken.") + " " +
                        UiLanguage.T("Abrufbar bis") + $" {expiresAt.ToLocalTime():dd.MM.yyyy}.";
        try
        {
            using var ms = new MemoryStream(QrCodeRenderer.PngBytes(url));
            _image.Source = new Bitmap(ms);
            _image.IsVisible = true;
        }
        catch
        {
            // A QR-rendering failure must never block the sale that already
            // completed - the link below stays readable.
        }
        _link.Text = url;
        _link.IsVisible = true;

        var timer = new System.Timers.Timer(60_000) { AutoReset = false };
        timer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Close);
        timer.Start();
        Closed += (_, _) => timer.Dispose();
    }

    public void ShowFailure(string reason, string consequence)
    {
        _heading.Text = "DIGITALBELEG NICHT MÖGLICH";
        _heading.Foreground = AppTheme.WarningAmber;
        _message.Text = $"{reason}\n\n{consequence}";
    }
}

/// <summary>R103/R104: shared QR-PNG rendering, used both by the main-screen popup and the Kundendisplay.</summary>
internal static class QrCodeRenderer
{
    public static byte[] PngBytes(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCoder.PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }
}
