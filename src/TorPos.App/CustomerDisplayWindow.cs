using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// R104: a genuine customer-facing second screen (e.g. an HP L7010t POS
/// touch monitor) - shows the live cart/running total while the cashier
/// scans, then a thank-you screen (with the R103 digital-receipt QR when
/// applicable) once a sale completes. Deliberately separate from
/// `OrderCustomerDisplayWindow` (the IMBISS pickup-number board), which by
/// design never shows cart/price/payment content at all.
/// </summary>
public sealed class CustomerDisplayWindow : Window
{
    private readonly int _screenIndex;
    private readonly string _companyName;
    private readonly DispatcherTimer _revertTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private readonly Panel _idlePanel;
    private readonly Panel _cartPanel;
    private readonly Panel _thankYouPanel;

    private readonly StackPanel _lines = new() { Spacing = 6 };
    private readonly TextBlock _itemCountText = new() { Opacity = 0.65, FontSize = 20 };
    private readonly TextBlock _totalText = new() { FontSize = 54, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private readonly TextBlock _discountText = new() { FontSize = 18, Opacity = 0.8, Foreground = Brushes.Orange };

    private readonly TextBlock _thankYouTotalText = new() { FontSize = 40, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private readonly Image _thankYouQrImage = new() { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private readonly TextBlock _thankYouQrHint = new()
    {
        Text = "Bon jetzt scannen (im WLAN dieses Geschäfts)",
        FontSize = 16, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center, IsVisible = false
    };

    public CustomerDisplayWindow(int screenIndex, string companyName)
    {
        _screenIndex = screenIndex;
        _companyName = string.IsNullOrWhiteSpace(companyName) ? "TOR POS" : companyName;

        Title = "TOR POS · Kundendisplay";
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        MinWidth = 800;
        MinHeight = 500;
        Background = new SolidColorBrush(Color.Parse("#07111F"));

        _idlePanel = BuildIdlePanel();
        _cartPanel = BuildCartPanel();
        _thankYouPanel = BuildThankYouPanel();

        Content = new Panel { Children = { _idlePanel, _cartPanel, _thankYouPanel } };

        _revertTimer.Tick += (_, _) => { _revertTimer.Stop(); ShowIdle(); };

        Opened += (_, _) =>
        {
            MoveToConfiguredScreen();
            WindowState = WindowState.FullScreen;
            ShowIdle();
        };
        Closed += (_, _) => _revertTimer.Stop();
    }

    public void ShowIdle()
    {
        _revertTimer.Stop();
        _idlePanel.IsVisible = true;
        _cartPanel.IsVisible = false;
        _thankYouPanel.IsVisible = false;
    }

    public void ShowCart(IReadOnlyList<CartLine> lines, long discountCents, long totalCents)
    {
        _revertTimer.Stop();
        _idlePanel.IsVisible = false;
        _thankYouPanel.IsVisible = false;
        _cartPanel.IsVisible = true;

        _lines.Children.Clear();
        foreach (var line in lines)
        {
            var name = line.ProductName + (string.IsNullOrWhiteSpace(line.VariantName) ? "" : " · " + line.VariantName);
            _lines.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{name}   ({line.Quantity:0.##} × {Formatting.Money(line.UnitPriceCents)})",
                        FontSize = 20, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = Formatting.Money(line.LineTotalCents),
                        FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                        [Grid.ColumnProperty] = 1
                    }
                }
            });
        }

        _itemCountText.Text = $"{lines.Count} Artikel";
        _discountText.Text = discountCents > 0 ? $"Rabatt: -{Formatting.Money(discountCents)}" : "";
        _totalText.Text = Formatting.Money(totalCents);
    }

    /// <param name="qrPayload">The digital-receipt URL, or null when no digital receipt applies to this sale.</param>
    public void ShowThankYou(long totalCents, string? qrPayload)
    {
        _idlePanel.IsVisible = false;
        _cartPanel.IsVisible = false;
        _thankYouPanel.IsVisible = true;

        _thankYouTotalText.Text = Formatting.Money(totalCents);

        if (qrPayload is not null)
        {
            try
            {
                using var ms = new MemoryStream(QrCodeRenderer.PngBytes(qrPayload));
                _thankYouQrImage.Source = new Bitmap(ms);
                _thankYouQrImage.IsVisible = true;
                _thankYouQrHint.IsVisible = true;
            }
            catch
            {
                _thankYouQrImage.IsVisible = false;
                _thankYouQrHint.IsVisible = false;
            }
        }
        else
        {
            _thankYouQrImage.IsVisible = false;
            _thankYouQrHint.IsVisible = false;
        }

        _revertTimer.Stop();
        _revertTimer.Start();
    }

    private Panel BuildIdlePanel() => new StackPanel
    {
        IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Spacing = 16,
        Children =
        {
            new TextBlock
            {
                Text = _companyName,
                FontSize = 48, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            new TextBlock
            {
                Text = "Willkommen!",
                FontSize = 26, Opacity = 0.7, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        }
    };

    private Panel BuildCartPanel()
    {
        var root = new Grid
        {
            IsVisible = false,
            Margin = new Thickness(36),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 16
        };

        root.Children.Add(new TextBlock
        {
            Text = _companyName,
            FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Brushes.White
        });

        var scroller = new ScrollViewer { Content = _lines, [Grid.RowProperty] = 1 };
        root.Children.Add(scroller);

        var footer = new StackPanel { Spacing = 4, [Grid.RowProperty] = 2 };
        footer.Children.Add(_itemCountText);
        footer.Children.Add(_discountText);
        footer.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children =
            {
                new TextBlock { Text = "GESAMT", FontSize = 30, Opacity = 0.75, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                new Border { Child = _totalText, [Grid.ColumnProperty] = 1 }
            }
        });
        root.Children.Add(footer);
        return root;
    }

    private Panel BuildThankYouPanel() => new StackPanel
    {
        IsVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Spacing = 18,
        Children =
        {
            new TextBlock
            {
                Text = "Vielen Dank!",
                FontSize = 46, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            },
            _thankYouTotalText,
            _thankYouQrImage,
            _thankYouQrHint
        }
    };

    private void MoveToConfiguredScreen()
    {
        var screens = Screens.All;
        if (screens.Count == 0) return;
        var index = _screenIndex <= 0
            ? (screens.Count > 1 ? 1 : 0)
            : Math.Clamp(_screenIndex - 1, 0, screens.Count - 1);
        var target = screens[index];
        Position = new PixelPoint(target.Bounds.X, target.Bounds.Y);
    }
}
