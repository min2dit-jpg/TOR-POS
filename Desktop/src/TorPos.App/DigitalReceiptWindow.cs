using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TorPos.App;

/// <summary>
/// R103: shown on screen (never printed) whenever BON EIN/AUS is off for a
/// sale, so the customer never leaves with nothing at all. The QR points at
/// the till's own local receipt server - reachable only over the shop's
/// own WiFi/LAN, not the public internet.
/// </summary>
public sealed class DigitalReceiptWindow : Window
{
    public DigitalReceiptWindow(string url)
    {
        Title = "Digitaler Bon";
        Width = 420;
        Height = 580;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var image = new Image { Width = 280, Height = 280, HorizontalAlignment = HorizontalAlignment.Center };
        try
        {
            using var ms = new MemoryStream(QrCodeRenderer.PngBytes(url));
            image.Source = new Bitmap(ms);
        }
        catch
        {
            // A QR-rendering failure must never block the sale that already
            // completed - just fall back to showing the plain link text.
        }

        var close = new Button
        {
            Content = "FERTIG",
            MinHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontWeight = FontWeight.Bold,
            FontSize = 18
        };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "BON JETZT SCANNEN",
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = "Mit dem Handy im WLAN dieses Geschäfts scannen, um den Bon als Webseite zu erhalten. Kein Papier, keine App nötig.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.75
                },
                image,
                new TextBlock
                {
                    Text = url,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.55
                },
                close
            }
        };

        Opened += (_, _) =>
        {
            UiLanguage.Apply(this);
            var timer = new System.Timers.Timer(45_000) { AutoReset = false };
            timer.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Close);
            timer.Start();
            Closed += (_, _) => timer.Dispose();
        };
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

/// <summary>
/// R103: best-effort LAN IPv4 lookup so the QR/URL points somewhere a
/// customer's phone (on the same shop WiFi) can actually reach - never
/// 127.0.0.1, which would only resolve on the till itself. On a
/// multi-adapter machine this picks the first "Up", non-loopback IPv4
/// address, which is right for the common single-NIC till but could pick
/// the wrong adapter on a machine with e.g. a VPN also active - a known,
/// acceptable v1 limitation.
/// </summary>
/// R115: the implementation moved to TorPos.Infrastructure.LocalNetworkAddress
/// so the receipt server and the printed QR URL can never disagree about which
/// address the till is reachable on. This file keeps only the doc comment
/// above for continuity.
