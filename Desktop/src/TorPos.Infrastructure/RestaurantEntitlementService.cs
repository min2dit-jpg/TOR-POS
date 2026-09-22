using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class RestaurantEntitlementService
{
    public const string PlusFeatureCode = "RESTAURANT_PLUS";

    private readonly ICommercialLicenseService _licenses;

    public RestaurantEntitlementService(
        ICommercialLicenseService licenses)
    {
        _licenses = licenses;
    }

    public RestaurantProductTier CurrentTier()
    {
        var status = _licenses.Check("RESTAURANT");
        if (!status.IsActive)
            return RestaurantProductTier.Restaurant;

        var plus = status.Features?.Any(
            feature => string.Equals(
                feature?.Trim(),
                PlusFeatureCode,
                StringComparison.OrdinalIgnoreCase)) == true;

        return plus
            ? RestaurantProductTier.RestaurantPlus
            : RestaurantProductTier.Restaurant;
    }

    public bool IsEnabled(
        RestaurantFeature feature) =>
        RestaurantProductFeatures.Includes(
            CurrentTier(),
            feature);

    public void Require(
        RestaurantFeature feature)
    {
        if (IsEnabled(feature))
            return;

        throw new InvalidOperationException(
            "Diese Funktion ist nur in TOR Restaurant Plus verfügbar.");
    }
}
