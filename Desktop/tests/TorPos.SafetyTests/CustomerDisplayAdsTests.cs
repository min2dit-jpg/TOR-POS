using TorPos.Core;

// Idle advertising on the customer display. The slides are pure data built
// from settings, the operator's pictures and the product catalogue; the
// checks below pin the rules that matter to a shop: nothing shows unless it
// is switched on, the order is the operator's, and a product card never
// advertises a price the till would not charge.
public static class CustomerDisplayAdsTests
{
    public static Task Run(Action<bool, string> assert)
    {
        // ---------- settings ----------
        var defaults = CustomerDisplayAds.ReadSettings(new Dictionary<string, string>());
        var clampedLow = CustomerDisplayAds.ReadSettings(new Dictionary<string, string>
        {
            [CustomerDisplayAds.EnabledKey] = "true",
            [CustomerDisplayAds.SourceKey] = "unbekannt",
            [CustomerDisplayAds.IntervalKey] = "1"
        });
        var clampedHigh = CustomerDisplayAds.ReadSettings(new Dictionary<string, string>
        {
            [CustomerDisplayAds.EnabledKey] = "True",
            [CustomerDisplayAds.SourceKey] = "artikel",
            [CustomerDisplayAds.IntervalKey] = "999"
        });
        assert(
            !defaults.Enabled &&
            defaults.Source == CustomerDisplayAds.SourceBoth &&
            defaults.Interval == TimeSpan.FromSeconds(CustomerDisplayAds.DefaultIntervalSeconds) &&
            clampedLow.Enabled && clampedLow.Source == CustomerDisplayAds.SourceBoth &&
            clampedLow.Interval == TimeSpan.FromSeconds(CustomerDisplayAds.MinIntervalSeconds) &&
            clampedHigh.Enabled && clampedHigh.Source == CustomerDisplayAds.SourceProducts &&
            clampedHigh.Interval == TimeSpan.FromSeconds(CustomerDisplayAds.MaxIntervalSeconds),
            "customer display advertising is off by default, and an unknown source or out-of-range interval falls back to safe values");

        // ---------- own pictures ----------
        var pictures = CustomerDisplayAds.BuildImageSlides(new[]
        {
            @"C:\Ads\02-doener.PNG",
            @"C:\Ads\notiz.txt",
            @"C:\Ads\01-menu.jpg",
            @"C:\Ads\video.mp4",
            @"C:\Ads\03-getraenke.webp"
        });
        assert(
            pictures.Select(x => Path.GetFileName(x.ImagePath)).SequenceEqual(new[] { "01-menu.jpg", "02-doener.PNG", "03-getraenke.webp" }) &&
            pictures.All(x => x.Kind == CustomerDisplaySlideKind.Image),
            "own advertising pictures are shown in file-name order and other files in the folder are ignored");

        var tooMany = CustomerDisplayAds.BuildImageSlides(
            Enumerable.Range(1, CustomerDisplayAds.MaxImageSlides + 10).Select(i => $@"C:\Ads\{i:000}.jpg"));
        assert(
            tooMany.Count == CustomerDisplayAds.MaxImageSlides,
            "the number of own advertising pictures is capped so a full folder cannot exhaust the display");

        // ---------- product cards ----------
        var doener = new Product { Id = 1, CategoryId = 1, Name = "Döner", BasePriceCents = 750, VatRate = 7m, ImagePath = @"C:\img\doener.jpg", SortOrder = 1 };
        var ayran = new Product { Id = 2, CategoryId = 2, Name = "Ayran", BasePriceCents = 250, VatRate = 7m, ImagePath = @"C:\img\ayran.png", SortOrder = 2 };
        var baklava = new Product { Id = 3, CategoryId = 3, Name = "Baklava", BasePriceCents = 1990, VatRate = 7m, Unit = "kg", ImagePath = @"C:\img\baklava.jpg", SortOrder = 3 };
        var menu = new Product
        {
            Id = 4, CategoryId = 1, Name = "Pide", BasePriceCents = 900, VatRate = 7m, ImagePath = @"C:\img\pide.jpg", SortOrder = 4,
            Variants = new[]
            {
                new ProductVariant(41, 4, "Groß", 1100),
                new ProductVariant(42, 4, "Klein", 800),
                new ProductVariant(43, 4, "Alt", 500, IsActive: false)
            }
        };
        var inactive = new Product { Id = 5, CategoryId = 1, Name = "Alt", BasePriceCents = 500, ImagePath = @"C:\img\alt.jpg", IsActive = false };
        var noPicture = new Product { Id = 6, CategoryId = 1, Name = "Ohne Bild", BasePriceCents = 500 };
        var missingFile = new Product { Id = 7, CategoryId = 1, Name = "Datei weg", BasePriceCents = 500, ImagePath = @"C:\img\weg.jpg" };
        var openPrice = new Product { Id = 8, CategoryId = 1, Name = "Freier Preis", BasePriceCents = 0, ImagePath = @"C:\img\frei.jpg" };

        var tenPercent = new PromotionSnapshot(9, "Woche", 10, "2026-09-01", "2026-09-30", PromotionScope.All, 0);
        var cards = CustomerDisplayAds.BuildProductSlides(
            new[] { menu, inactive, doener, noPicture, missingFile, openPrice, ayran, baklava },
            product => product.Id is 1 or 3 ? tenPercent : null,
            path => !path.EndsWith("weg.jpg", StringComparison.Ordinal));

        assert(
            cards.Select(x => x.Title).SequenceEqual(new[] { "Döner", "Ayran", "Baklava", "Pide" }) &&
            cards.All(x => x.Kind == CustomerDisplaySlideKind.Product),
            "only active products with an existing picture and a price become cards, in the till's article order");

        var engine = new SaleEngine();
        engine.Add(doener, quantity: 1m, promotion: tenPercent);
        var charged = engine.Cart.Single();
        var doenerCard = cards.Single(x => x.Title == "Döner");
        var ayranCard = cards.Single(x => x.Title == "Ayran");
        assert(
            doenerCard.PriceText == "6,75 €" && charged.UnitPriceCents == 675 &&
            doenerCard.OldPriceText == "7,50 €" && doenerCard.Badge == "-10 %" &&
            ayranCard.PriceText == "2,50 €" && ayranCard.OldPriceText == "" && ayranCard.Badge == "",
            "a product card shows exactly the price the checkout charges, with the list price and badge only while an Angebot is active");

        var baklavaCard = cards.Single(x => x.Title == "Baklava");
        var pideCard = cards.Single(x => x.Title == "Pide");
        assert(
            baklavaCard.PriceText == "17,91 € / kg" && baklavaCard.OldPriceText == "19,90 € / kg" &&
            pideCard.PriceText == "ab 8,00 €",
            "weighed articles are advertised per kg and articles with variants from their cheapest active variant");

        var capped = CustomerDisplayAds.BuildProductSlides(
            new[] { doener, ayran, baklava, menu },
            _ => null,
            _ => true,
            max: 2);
        assert(
            capped.Count == 2,
            "the number of product cards is capped");

        // ---------- source selection ----------
        var on = CustomerDisplayAds.ReadSettings(new Dictionary<string, string> { [CustomerDisplayAds.EnabledKey] = "true" });
        var onlyImages = on with { Source = CustomerDisplayAds.SourceImages };
        var onlyProducts = on with { Source = CustomerDisplayAds.SourceProducts };
        var off = on with { Enabled = false };
        assert(
            CustomerDisplayAds.Combine(on, pictures, cards).Count == pictures.Count + cards.Count &&
            CustomerDisplayAds.Combine(on, pictures, cards)[0].Kind == CustomerDisplaySlideKind.Image &&
            CustomerDisplayAds.Combine(onlyImages, pictures, cards).Count == pictures.Count &&
            CustomerDisplayAds.Combine(onlyProducts, pictures, cards).Count == cards.Count &&
            CustomerDisplayAds.Combine(off, pictures, cards).Count == 0,
            "the chosen source decides what is shown, own pictures come first, and switched off means the plain welcome screen");

        // ---------- which monitor the customer window uses ----------
        // Windows may list the primary (till) screen second. The automatic
        // setting must still pick the other monitor, never the till's own.
        var primarySecond = new[]
        {
            new DisplayScreenInfo(IsPrimary: false, X: 1920, Y: 0),
            new DisplayScreenInfo(IsPrimary: true, X: 0, Y: 0)
        };
        assert(
            DisplayScreenSelection.Choose(primarySecond, configured: 0, tillScreenIndex: 1) == 0 &&
            DisplayScreenSelection.Choose(primarySecond, configured: 0, tillScreenIndex: null) == 0,
            "the automatic customer display never opens on the till's screen, whatever order Windows lists the monitors in");

        var single = new[] { new DisplayScreenInfo(true, 0, 0) };
        var tillOnSecondary = new[]
        {
            new DisplayScreenInfo(true, 0, 0),
            new DisplayScreenInfo(false, 1920, 0)
        };
        assert(
            DisplayScreenSelection.Choose(single, 0, 0) is null &&
            DisplayScreenSelection.Choose(single, 0, null) is null &&
            DisplayScreenSelection.Choose(tillOnSecondary, 0, 1) == 0,
            "with only one monitor the automatic setting finds no target, and a till on the secondary monitor sends the customer window to the other one");

        var three = new[]
        {
            new DisplayScreenInfo(false, 1920, 0),
            new DisplayScreenInfo(true, 0, 0),
            new DisplayScreenInfo(false, -1280, 0)
        };
        assert(
            DisplayScreenSelection.Choose(three, 1, null) == 1 &&
            DisplayScreenSelection.Choose(three, 2, null) == 2 &&
            DisplayScreenSelection.Choose(three, 3, null) == 0 &&
            DisplayScreenSelection.Choose(three, 4, null) == 0,
            "screen numbers 1-4 are stable: 1 is the Windows primary screen, the others follow from left to right");

        // ---------- back to the advertising after a checkout ----------
        // A TEST/simulation checkout (no TSE yet) once cleared the cart
        // without telling the display, so it froze on the paid cart and
        // the slides never came back.
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var display = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/CustomerDisplayWindow.cs"));
        var clear = Body(main, "private void ClearCompletedCart()");
        assert(
            clear.Contains("_customerDisplayWindow?.ShowThankYou(_engine.TotalCents, null);", StringComparison.Ordinal) &&
            clear.IndexOf("ShowThankYou", StringComparison.Ordinal) < clear.IndexOf("_engine.Clear()", StringComparison.Ordinal),
            "every completed checkout, TEST mode included, shows the thank-you screen with the paid total, which then returns to the advertising");

        var cleared = Body(display, "public void ShowCartCleared()");
        assert(
            main.Contains("_customerDisplayWindow?.ShowCartCleared();", StringComparison.Ordinal) &&
            cleared.Contains("_cartPanel.IsVisible", StringComparison.Ordinal) &&
            cleared.Contains("ShowIdle()", StringComparison.Ordinal),
            "a cart emptied without a sale returns the customer display to the advertising, without cutting a thank-you screen short");

        var keyboard = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/TouchKeyboard.cs"));
        assert(
            keyboard.Contains("if (w is CustomerDisplayWindow or OrderCustomerDisplayWindow) return;", StringComparison.Ordinal) &&
            keyboard.Contains("bar.IsVisible = !AutoOpen", StringComparison.Ordinal),
            "the customer screens carry no TASTATUR bar, and the till shows it only when the keyboard does not open on touch");

        assert(
            display.Contains("internal const int ThankYouSeconds = 10;", StringComparison.Ordinal) &&
            display.Contains("qrPayload is null ? ThankYouSeconds : ThankYouWithQrSeconds", StringComparison.Ordinal),
            "the thank-you screen gives way to the advertising after 10 seconds, and stays longer only while a receipt QR code is shown");

        return Task.CompletedTask;
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }
        return "";
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new FileNotFoundException(relativePath);
    }
}
