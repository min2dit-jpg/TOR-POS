namespace TorPos.Core;

/// <summary>
/// Idle advertising on the customer display: while no customer is being
/// served, the second screen rotates the operator's own pictures and/or
/// product cards (picture, name, price, active Angebot).
///
/// Everything here is pure so the rules can be tested without a screen. The
/// prices come from the same SaleEngine the till uses, so a card can never
/// advertise a price the checkout would not charge.
/// </summary>
public static class CustomerDisplayAds
{
    public const string EnabledKey = "device.customer_display.ads.enabled";
    public const string SourceKey = "device.customer_display.ads.source";
    public const string IntervalKey = "device.customer_display.ads.interval_seconds";

    public const string SourceBoth = "BEIDE";
    public const string SourceImages = "BILDER";
    public const string SourceProducts = "ARTIKEL";

    public const int DefaultIntervalSeconds = 8;
    public const int MinIntervalSeconds = 4;
    public const int MaxIntervalSeconds = 60;

    /// <summary>Upper bound for generated product cards, so a large catalogue
    /// never turns the idle screen into an endless loop or a memory problem.</summary>
    public const int MaxProductSlides = 40;

    /// <summary>Upper bound for the operator's own pictures.</summary>
    public const int MaxImageSlides = 60;

    public static readonly IReadOnlyList<string> ImageExtensions =
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };

    public static CustomerDisplayAdSettings ReadSettings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var enabled =
            values.TryGetValue(EnabledKey, out var rawEnabled) &&
            bool.TryParse(rawEnabled, out var parsedEnabled) &&
            parsedEnabled;

        var source = values.TryGetValue(SourceKey, out var rawSource)
            ? (rawSource ?? "").Trim().ToUpperInvariant()
            : "";
        if (source is not (SourceBoth or SourceImages or SourceProducts))
            source = SourceBoth;

        var interval = DefaultIntervalSeconds;
        if (values.TryGetValue(IntervalKey, out var rawInterval) &&
            int.TryParse(rawInterval, out var parsedInterval))
        {
            interval = Math.Clamp(parsedInterval, MinIntervalSeconds, MaxIntervalSeconds);
        }

        return new CustomerDisplayAdSettings(enabled, source, TimeSpan.FromSeconds(interval));
    }

    public static bool IsSupportedImage(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The operator's own pictures, in file-name order so "01-…", "02-…"
    /// controls the sequence.
    /// </summary>
    public static IReadOnlyList<CustomerDisplaySlide> BuildImageSlides(IEnumerable<string> files) =>
        files
            .Where(IsSupportedImage)
            .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
            .Take(MaxImageSlides)
            .Select(CustomerDisplaySlide.ForImage)
            .ToArray();

    /// <summary>
    /// One card per active product that has a picture and a positive price.
    /// The shown price is computed by <see cref="SaleEngine"/> with the
    /// product's current Angebot, exactly as a scan would put it in the cart.
    /// </summary>
    public static IReadOnlyList<CustomerDisplaySlide> BuildProductSlides(
        IEnumerable<Product> products,
        Func<Product, PromotionSnapshot?> promotionFor,
        Func<string, bool> imageExists,
        int max = MaxProductSlides)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(promotionFor);
        ArgumentNullException.ThrowIfNull(imageExists);

        var result = new List<CustomerDisplaySlide>();
        foreach (var product in products
                     .Where(x => x.IsActive && IsSupportedImage(x.ImagePath))
                     .OrderBy(x => x.SortOrder)
                     .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (result.Count >= Math.Max(0, max))
                break;
            if (!imageExists(product.ImagePath))
                continue;

            var slide = ProductSlide(product, promotionFor(product));
            if (slide is not null)
                result.Add(slide);
        }

        return result;
    }

    /// <summary>
    /// Own pictures first, then product cards, according to the chosen source.
    /// An empty result means the display shows its plain welcome screen.
    /// </summary>
    public static IReadOnlyList<CustomerDisplaySlide> Combine(
        CustomerDisplayAdSettings settings,
        IReadOnlyList<CustomerDisplaySlide> imageSlides,
        IReadOnlyList<CustomerDisplaySlide> productSlides)
    {
        if (!settings.Enabled)
            return Array.Empty<CustomerDisplaySlide>();

        return settings.Source switch
        {
            SourceImages => imageSlides.ToArray(),
            SourceProducts => productSlides.ToArray(),
            _ => imageSlides.Concat(productSlides).ToArray()
        };
    }

    private static CustomerDisplaySlide? ProductSlide(Product product, PromotionSnapshot? promotion)
    {
        try
        {
            // Variants: advertise the cheapest one as "ab …".
            var variant = product.Variants
                .Where(x => x.IsActive && x.PriceCents > 0)
                .OrderBy(x => x.PriceCents)
                .FirstOrDefault();
            if (product.Variants.Count > 0 && variant is null)
                return null;
            if (variant is null && product.BasePriceCents <= 0)
                return null;

            var engine = new SaleEngine();
            engine.Add(product, variant, quantity: 1m, promotion: promotion);
            var line = engine.Cart.SingleOrDefault();
            if (line is null || line.UnitPriceCents <= 0)
                return null;

            var suffix = product.IsWeighted ? " / kg" : "";
            var prefix = variant is not null ? "ab " : "";
            var price = prefix + Money(line.UnitPriceCents) + suffix;
            var oldPrice = line.HasPromotion && line.EffectiveListUnitPriceCents > line.UnitPriceCents
                ? Money(line.EffectiveListUnitPriceCents) + suffix
                : "";
            var badge = oldPrice.Length > 0 ? $"-{line.PromotionPercent} %" : "";

            return new CustomerDisplaySlide(
                CustomerDisplaySlideKind.Product,
                product.ImagePath,
                product.Name,
                price,
                oldPrice,
                badge);
        }
        catch (InvalidOperationException)
        {
            // A product the engine would refuse (e.g. inconsistent menu setup)
            // is simply not advertised.
            return null;
        }
    }

    private static string Money(long cents) =>
        (cents / 100m).ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " €";
}

public sealed record CustomerDisplayAdSettings(bool Enabled, string Source, TimeSpan Interval);

public enum CustomerDisplaySlideKind { Image, Product }

public sealed record CustomerDisplaySlide(
    CustomerDisplaySlideKind Kind,
    string ImagePath,
    string Title,
    string PriceText,
    string OldPriceText,
    string Badge)
{
    public static CustomerDisplaySlide ForImage(string path) =>
        new(CustomerDisplaySlideKind.Image, path, "", "", "", "");
}
