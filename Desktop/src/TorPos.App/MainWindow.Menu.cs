using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TorPos.Core;

namespace TorPos.App;

// R64: Menü-/Kategorien-Bildschirm (Kategorie- und Artikel-Kacheln, Paginierung,
// Bild-Cache) aus MainWindow.axaml.cs herausgelöst. Reines Verschieben von Code,
// keine Verhaltensänderung. Erleichtert künftige Änderungen am Kassen-Menü,
// ohne die 3000+ Zeilen lange MainWindow.axaml.cs anfassen zu müssen.
public partial class MainWindow
{
    private static Grid CreateButtonGrid(int columns, int rows)
    {
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                string.Join(",", Enumerable.Repeat("*", columns))),
            RowDefinitions = new RowDefinitions(
                string.Join(",", Enumerable.Repeat("*", rows)))
        };
    }

    // R64: zentrale Paginierungs-Formel für die Warengruppen-Kacheln.
    // Die Kategorie-Berechnung war vorher in BuildCategories,
    // OnCategoryNextClick und EnsureSelectedCategoryVisible dupliziert; eine Änderung an
    // Spalten-/Zeilen-Grenzen musste dadurch überall einzeln nachgezogen werden.
    private readonly record struct GridPaging(int Columns, int Rows, int PageSize, int PageCount)
    {
        public int ClampPage(int page) => Math.Clamp(page, 0, PageCount - 1);
    }

    private static GridPaging ComputePaging(int columns, int rows, int itemCount)
    {
        var pageSize = Math.Max(1, columns * rows);
        var pageCount = Math.Max(1, (int)Math.Ceiling(itemCount / (double)pageSize));
        return new GridPaging(columns, rows, pageSize, pageCount);
    }

    private GridPaging CategoryPaging()
    {
        var columns = ModeUiInt("ui.category.columns", 4, 5, 4, 2, 8);
        var rows = ModeUiInt("ui.category.rows", 6, 6, 4, 1, 10);
        return ComputePaging(columns, rows, _catalog.Categories.Count);
    }

    private GridPaging ProductPaging()
    {
        var columns = ModeUiInt("ui.product.columns", 4, 5, 4, 2, 8);
        var rows = ModeUiInt("ui.product.rows", 10, 8, 3, 2, 12);
        var count = _categoryId == 0 ? 0 : _catalog.GetByCategory(_categoryId).Count;
        return ComputePaging(columns, rows, count);
    }

    private void BuildCategories()
    {
        var paging = CategoryPaging();
        var columns = paging.Columns;
        var rows = paging.Rows;
        var pageSize = paging.PageSize;
        var pageCount = paging.PageCount;

        _categoryPage = paging.ClampPage(_categoryPage);

        var grid = CreateButtonGrid(columns, rows);
        var visible = _catalog.Categories
            .Skip(_categoryPage * pageSize)
            .Take(pageSize)
            .ToArray();

        for (var i = 0; i < pageSize; i++)
        {
            var row = i / columns;
            var column = i % columns;

            if (i < visible.Length)
            {
                var category = visible[i];
                var button = new Button
                {
                    Content = CategoryTileContent(category.Name, category.TileColor),
                    Tag = category.Id,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(ParseCategoryColor(category.TileColor)),
                    BorderBrush = new SolidColorBrush(CategoryBorderColor(category.TileColor))
                };
                button.Classes.Add("categorytile");
                button.Click += OnCategoryClick;
                Grid.SetRow(button, row);
                Grid.SetColumn(button, column);
                grid.Children.Add(button);
            }
            // R64 FIX1: unbelegte Plätze bleiben wirklich leer. Dadurch wirkt
            // eine dünn besetzte Warengruppen-Seite nicht wie ein Raster aus
            // deaktivierten Schaltflächen.
        }

        CategoryGrid.Children.Clear();
        CategoryGrid.Children.Add(grid);

        CategoryPageText.Text = $"{_categoryPage + 1} / {pageCount}";
        CategoryPrevButton.IsEnabled = _categoryPage > 0;
        CategoryNextButton.IsEnabled = _categoryPage < pageCount - 1;
    }

    private static Color ParseCategoryColor(string? value)
    {
        try
        {
            var text = (value ?? "").Trim();
            if (text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit))
                return Color.Parse(text);
        }
        catch { }
        return Color.Parse("#17466A");
    }


    private static Color CategoryTextColor(string? value)
    {
        var c = ParseCategoryColor(value);
        // WCAG-nahe Helligkeitsentscheidung: freie Kundenfarben bleiben gut lesbar.
        var luminance = (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
        return luminance > 165
            ? Color.Parse("#111827")
            : Colors.White;
    }

    private static Color CategoryBorderColor(string? value)
    {
        var c = ParseCategoryColor(value);
        return Color.FromRgb(
            (byte)Math.Min(255, c.R + 55),
            (byte)Math.Min(255, c.G + 55),
            (byte)Math.Min(255, c.B + 55));
    }

    private static Color DarkenCategoryColor(string? value)
    {
        var c = ParseCategoryColor(value);
        return Color.FromRgb(
            (byte)(c.R * .72),
            (byte)(c.G * .72),
            (byte)(c.B * .72));
    }

    private Control CategoryTileContent(string categoryName, string tileColor)
    {
        var name = string.IsNullOrWhiteSpace(categoryName)
            ? "WARENGRUPPE"
            : categoryName.Trim();

        var firstChar = name.FirstOrDefault(char.IsLetterOrDigit);
        var first = firstChar == default
            ? "•"
            : char.ToUpperInvariant(firstChar).ToString();

        var panel = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            BorderBrush = new SolidColorBrush(CategoryBorderColor(tileColor)),
            BorderThickness = new Thickness(1.4),
            Background = new SolidColorBrush(DarkenCategoryColor(tileColor)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = first,
                FontSize = 21,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });

        panel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = Math.Clamp(
                UiInt("ui.touch.font_size", 18, 11, 28),
                14,
                22),
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 2
        });

        return panel;
    }

    private void OnCategoryClick(object? sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:long id})
        {
            _productPage = 0;
            SelectCategory(id);
            ShowProductPanel();
        }
    }

    private void ShowCategoryOverview()
    {
        CategoryArea.IsVisible = true;
        ProductArea.IsVisible = false;
    }

    private void ShowProductPanel()
    {
        CategoryArea.IsVisible = false;
        ProductArea.IsVisible = true;
    }

    private void OnBackToCategoriesClick(object? sender, RoutedEventArgs e)
    {
        ShowCategoryOverview();
        BuildCategories();
    }

    private void OnCategoryPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_categoryPage <= 0)
            return;

        _categoryPage--;
        BuildCategories();
    }

    private void OnCategoryNextClick(object? sender, RoutedEventArgs e)
    {
        var pageCount = CategoryPaging().PageCount;

        if (_categoryPage >= pageCount - 1)
            return;

        _categoryPage++;
        BuildCategories();
    }

    private void SelectCategory(long id)
    {
        _categoryId = id;

        var category = _catalog.Categories.FirstOrDefault(x => x.Id == id);
        SelectedCategoryText.Text = category is null
            ? "ARTIKEL"
            : $"ARTIKEL · {category.Name.ToUpperInvariant()}";

        EnsureSelectedCategoryVisible();
        BuildProducts();
    }

    private void EnsureSelectedCategoryVisible()
    {
        if (_categoryId == 0)
            return;

        var index = _catalog.Categories
            .Select((x, i) => new { x.Id, Index = i })
            .FirstOrDefault(x => x.Id == _categoryId)?.Index;

        if (index is null)
            return;

        _categoryPage = index.Value / CategoryPaging().PageSize;
    }

    private void BuildProducts()
    {
        ProductGrid.Children.Clear();

        if (_categoryId == 0)
        {
            ProductPageText.Text = "0 / 0";
            ProductPrevButton.IsEnabled = false;
            ProductNextButton.IsEnabled = false;
            return;
        }

        var paging = ProductPaging();
        var columns = paging.Columns;
        var rows = paging.Rows;
        var pageSize = paging.PageSize;
        var products = _catalog.GetByCategory(_categoryId);
        var pageCount = paging.PageCount;

        _productPage = paging.ClampPage(_productPage);

        var visible = products
            .Skip(_productPage * pageSize)
            .Take(pageSize)
            .ToArray();

        // R58: Eine dünn besetzte Artikelseite soll nicht wie ein Raster aus vielen
        // leeren Schaltflächen wirken. Die Seitengröße/Paginierung bleibt unverändert,
        // nur die sichtbaren Zeilen werden für die aktuelle Seite verdichtet.
        var visualRows = ProductVisualRowsForCount(columns, rows, visible.Length);
        var grid = CreateButtonGrid(columns, visualRows);
        var compact = visualRows >= 5;

        for (var i = 0; i < visible.Length; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var cell = ProductButton(visible[i], compact);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        ProductGrid.Children.Add(grid);
        ProductPageText.Text = $"{_productPage + 1} / {pageCount}";
        ProductPrevButton.IsEnabled = _productPage > 0;
        ProductNextButton.IsEnabled = _productPage < pageCount - 1;
    }

    internal static int ProductVisualRowsForCount(int columns, int configuredRows, int visibleCount)
    {
        columns = Math.Max(1, columns);
        configuredRows = Math.Max(1, configuredRows);

        // Mindestens drei ruhige Zeilen verhindern übergroße Einzelkacheln;
        // volle Seiten behalten die vom Betrieb konfigurierte Zeilenzahl.
        var needed = Math.Max(1, (int)Math.Ceiling(Math.Max(0, visibleCount) / (double)columns));
        return Math.Min(configuredRows, Math.Max(Math.Min(3, configuredRows), needed));
    }

    private void OnProductPrevClick(object? sender, RoutedEventArgs e)
    {
        if (_productPage <= 0)
            return;

        _productPage--;
        BuildProducts();
    }

    private void OnProductNextClick(object? sender, RoutedEventArgs e)
    {
        if (_categoryId == 0)
            return;

        var pageCount = ProductPaging().PageCount;

        if (_productPage >= pageCount - 1)
            return;

        _productPage++;
        BuildProducts();
    }

    private Control ProductButton(Product p, bool compact)
    {
        var fontSize = UiInt("ui.touch.font_size", 18, 11, 28);
        var showImages = _settingsCache.GetBool("ui.product.show_images", true) && !compact;

        var button = new Button
        {
            Tag = p
        };
        button.Classes.Add("producttile");
        button.Click += OnProductClick;

        var price = p.Variants.Count > 0
            ? p.Variants.Min(x => x.PriceCents) + p.PfandCents
            : p.BasePriceCents + p.PfandCents;
        var priceLabel = p.IsWeighted
            ? $"{Formatting.Money(p.BasePriceCents)} / kg"
            : p.Variants.Count > 0
                ? $"ab {Formatting.Money(price)}"
                : Formatting.Money(price);

        var image = showImages ? LoadImage(p.ImagePath) : null;
        if (image is not null)
        {
            var imageGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("1.25*,Auto,Auto"),
                RowSpacing = 4
            };
            imageGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#20364B")),
                CornerRadius = new CornerRadius(7),
                ClipToBounds = true,
                Child = new Image
                {
                    Source = image,
                    Stretch = Stretch.UniformToFill
                }
            });

            var name = new TextBlock
            {
                Text = p.Name,
                FontSize = Math.Max(12, fontSize - 3),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxLines = 2
            };
            Grid.SetRow(name, 1);
            imageGrid.Children.Add(name);

            var priceText = ProductPriceText(priceLabel, Math.Max(12, fontSize - 3));
            Grid.SetRow(priceText, 2);
            imageGrid.Children.Add(priceText);
            button.Content = imageGrid;
            return button;
        }

        // Ohne echtes Artikelbild kein künstlich geteiltes Kartenlayout mehr.
        // Initiale, Name und Preis bilden eine einzige ruhige, zentrierte Touch-Kachel.
        var initialChar = p.Name.FirstOrDefault(char.IsLetterOrDigit);
        var initial = initialChar == default ? "•" : char.ToUpperInvariant(initialChar).ToString();
        var panel = new StackPanel
        {
            Spacing = compact ? 4 : 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!compact)
        {
            panel.Children.Add(new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(23),
                Background = new SolidColorBrush(Color.Parse("#27445F")),
                BorderBrush = new SolidColorBrush(Color.Parse("#4E7897")),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = initial,
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#DCEEFF")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = p.Name,
            FontSize = Math.Max(12, fontSize - (compact ? 2 : 1)),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxLines = 2
        });
        panel.Children.Add(ProductPriceText(priceLabel, Math.Max(12, fontSize - 2)));
        button.Content = panel;
        return button;
    }

    private static TextBlock ProductPriceText(string text, double fontSize) =>
        new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = AppTheme.AccentTeal,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        };

    // R87: product tiles never render an image larger than a few hundred
    // pixels wide, but a customer-supplied photo can easily be 3000+ px from
    // a phone camera. Decoding every catalog image at full resolution just
    // to cache-and-shrink it in the renderer wastes memory/CPU on every
    // catalog page switch, and gets worse with larger catalogs (see R84's
    // 10k-product benchmark). Bitmap.DecodeToWidth asks Avalonia's own
    // codec to decode straight to a bounded size - no extra dependency, no
    // on-disk thumbnail file to keep in sync with the source image.
    private const int ThumbnailWidthPx = 480;

    private Bitmap? LoadImage(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)) return null;
        if(_imageCache.TryGetValue(path,out var cached)) return cached;
        try
        {
            using var stream=File.OpenRead(path);
            var bmp=Bitmap.DecodeToWidth(stream, ThumbnailWidthPx, BitmapInterpolationMode.HighQuality);
            _imageCache[path]=bmp;
            return bmp;
        }
        catch{return null;}
    }
}
