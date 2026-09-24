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

        return Task.CompletedTask;
    }
}
