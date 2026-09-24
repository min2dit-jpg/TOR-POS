using System.IO;
using Avalonia;
using Avalonia.Animation;
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
    private readonly Control _welcomeContent;

    // Idle advertising: rotating own pictures / product cards while no
    // customer is being served. Only the current picture is kept decoded, so a
    // large slide list never costs more than one bitmap of memory.
    private readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromSeconds(CustomerDisplayAds.DefaultIntervalSeconds) };
    private IReadOnlyList<CustomerDisplaySlide> _slides = Array.Empty<CustomerDisplaySlide>();
    private int _slideIndex = -1;
    private int _slideGeneration;
    private Bitmap? _currentSlideBitmap;
    private readonly Grid _slideHost = new() { IsVisible = false, Opacity = 1 };
    private readonly Image _fullImage = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly Grid _productCard = new() { IsVisible = false, Margin = new Thickness(48), ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 48 };
    private readonly Image _productImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _productTitle = new() { FontSize = 52, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _productPrice = new() { FontSize = 80, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private readonly TextBlock _productOldPrice = new() { FontSize = 34, Opacity = 0.7, Foreground = Brushes.White, TextDecorations = TextDecorations.Strikethrough };
    private readonly TextBlock _productBadgeText = new() { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    private readonly Border _productBadge;
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
        Text = "Ihr digitaler Kassenbon - jetzt mit dem Handy scannen",
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

        _productBadge = new Border
        {
            Background = Brushes.OrangeRed,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _productBadgeText
        };
        _welcomeContent = BuildWelcomeContent();
        _idlePanel = BuildIdlePanel();
        _cartPanel = BuildCartPanel();
        _thankYouPanel = BuildThankYouPanel();

        Content = new Panel { Children = { _idlePanel, _cartPanel, _thankYouPanel } };

        _revertTimer.Tick += (_, _) => { _revertTimer.Stop(); ShowIdle(); };
        _slideTimer.Tick += async (_, _) => await AdvanceSlideAsync();

        Opened += (_, _) =>
        {
            MoveToConfiguredScreen();
            WindowState = WindowState.FullScreen;
            ShowIdle();
        };
        Closed += (_, _) =>
        {
            _revertTimer.Stop();
            _slideTimer.Stop();
            _slideGeneration++;
            ReplaceSlideBitmap(null);
        };
    }

    /// <summary>
    /// Replaces the advertising slides. An empty list (or advertising switched
    /// off) shows the plain welcome screen again.
    /// </summary>
    public void SetSlides(IReadOnlyList<CustomerDisplaySlide> slides, TimeSpan interval)
    {
        _slides = slides ?? Array.Empty<CustomerDisplaySlide>();
        _slideTimer.Interval = interval < TimeSpan.FromSeconds(CustomerDisplayAds.MinIntervalSeconds)
            ? TimeSpan.FromSeconds(CustomerDisplayAds.MinIntervalSeconds)
            : interval;
        _slideIndex = -1;
        _slideGeneration++;
        if (_idlePanel.IsVisible)
            StartSlides();
    }

    public void ShowIdle()
    {
        _revertTimer.Stop();
        _idlePanel.IsVisible = true;
        _cartPanel.IsVisible = false;
        _thankYouPanel.IsVisible = false;
        StartSlides();
    }

    private void StartSlides()
    {
        _slideTimer.Stop();
        if (_slides.Count == 0)
        {
            ShowWelcome();
            return;
        }

        _ = AdvanceSlideAsync();
        if (_slides.Count > 1)
            _slideTimer.Start();
    }

    private void StopSlides()
    {
        _slideTimer.Stop();
        _slideGeneration++;
    }

    private void ShowWelcome()
    {
        _slideHost.IsVisible = false;
        _welcomeContent.IsVisible = true;
        ReplaceSlideBitmap(null);
    }

    private async Task AdvanceSlideAsync()
    {
        if (!_idlePanel.IsVisible || _slides.Count == 0)
            return;

        var generation = ++_slideGeneration;
        var slides = _slides;
        // Try each slide at most once per step: a deleted or broken picture is
        // skipped instead of leaving the screen blank.
        for (var attempt = 0; attempt < slides.Count; attempt++)
        {
            _slideIndex = (_slideIndex + 1) % slides.Count;
            var slide = slides[_slideIndex];
            var path = slide.ImagePath;
            var bitmap = await Task.Run(() => LoadBitmap(path));

            if (generation != _slideGeneration || !_idlePanel.IsVisible)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is null)
                continue;

            if (_slideHost.IsVisible)
            {
                _slideHost.Opacity = 0;
                await Task.Delay(350);
                if (generation != _slideGeneration || !_idlePanel.IsVisible)
                {
                    bitmap.Dispose();
                    return;
                }
            }

            ShowSlide(slide, bitmap);
            _slideHost.Opacity = 1;
            return;
        }

        ShowWelcome();
    }

    private void ShowSlide(CustomerDisplaySlide slide, Bitmap bitmap)
    {
        if (slide.Kind == CustomerDisplaySlideKind.Product)
        {
            _fullImage.IsVisible = false;
            _fullImage.Source = null;
            _productImage.Source = bitmap;
            _productTitle.Text = slide.Title;
            _productPrice.Text = slide.PriceText;
            _productOldPrice.Text = slide.OldPriceText;
            _productOldPrice.IsVisible = slide.OldPriceText.Length > 0;
            _productBadgeText.Text = slide.Badge;
            _productBadge.IsVisible = slide.Badge.Length > 0;
            _productCard.IsVisible = true;
        }
        else
        {
            _productCard.IsVisible = false;
            _productImage.Source = null;
            _fullImage.Source = bitmap;
            _fullImage.IsVisible = true;
        }

        ReplaceSlideBitmap(bitmap);
        _welcomeContent.IsVisible = false;
        _slideHost.IsVisible = true;
    }

    private void ReplaceSlideBitmap(Bitmap? next)
    {
        var previous = _currentSlideBitmap;
        _currentSlideBitmap = next;
        if (next is null)
        {
            _fullImage.Source = null;
            _productImage.Source = null;
        }

        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            // Decoded at screen width, not at the camera's original size.
            return Bitmap.DecodeToWidth(stream, 1920, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    public void ShowCart(IReadOnlyList<CartLine> lines, long discountCents, long totalCents)
    {
        _revertTimer.Stop();
        StopSlides();
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
                        Text = line.IsWeighted
                            ? $"{name}   ({WeightedSales.QuantityLabel(line.Quantity)} × {Formatting.Money(line.UnitPriceCents)}/kg)"
                            : $"{name}   ({line.Quantity:0.##} × {Formatting.Money(line.UnitPriceCents)})",
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
        StopSlides();
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

        // R145: a QR code needs time to be scanned.
        _revertTimer.Interval = TimeSpan.FromSeconds(qrPayload is null ? 20 : 60);
        _revertTimer.Stop();
        _revertTimer.Start();
    }

    private Panel BuildIdlePanel()
    {
        _slideHost.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) }
        };

        var details = new StackPanel
        {
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 1,
            Children = { _productBadge, _productTitle, _productOldPrice, _productPrice }
        };
        _productCard.Children.Add(_productImage);
        _productCard.Children.Add(details);

        _slideHost.Children.Add(_fullImage);
        _slideHost.Children.Add(_productCard);

        return new Grid
        {
            IsVisible = false,
            Children = { _welcomeContent, _slideHost }
        };
    }

    private Control BuildWelcomeContent() => new StackPanel
    {
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
