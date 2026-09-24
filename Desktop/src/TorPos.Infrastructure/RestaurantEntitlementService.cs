using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class RestaurantEntitlementService
{
    public const string PlusFeatureCode = "RESTAURANT_PLUS";
    public const string SelfOrderFeatureCode = "RESTAURANT_SELF_ORDER";

    private readonly ICommercialLicenseService _licenses;

    public RestaurantEntitlementService(
        ICommercialLicenseService licenses)
    {
        _licenses = licenses;
    }

    public RestaurantProductTier CurrentTier() =>
        ResolveTier(_licenses.Check("RESTAURANT"));

    public bool IsEnabled(
        RestaurantFeature feature)
    {
        var status = _licenses.Check("RESTAURANT");
        if (!status.IsActive)
            return false;

        var tier = ResolveTier(status);

        if (RestaurantProductFeatures.IsAddOn(feature))
        {
            return feature == RestaurantFeature.QrTischbestellung &&
                   tier == RestaurantProductTier.RestaurantPlus &&
                   HasFeature(status, SelfOrderFeatureCode);
        }

        return RestaurantProductFeatures.Includes(
            tier,
            feature);
    }

    public void Require(
        RestaurantFeature feature)
    {
        if (IsEnabled(feature))
            return;

        if (RestaurantProductFeatures.IsAddOn(feature))
        {
            throw new InvalidOperationException(
                "Diese Funktion benötigt TOR Restaurant Plus und das TOR Self Order Add-on.");
        }

        throw new InvalidOperationException(
            "Diese Funktion ist nur in TOR Restaurant Plus verfügbar.");
    }

    private static RestaurantProductTier ResolveTier(
        CommercialLicenseStatus status)
    {
        if (!status.IsActive)
            return RestaurantProductTier.Restaurant;

        return HasFeature(status, PlusFeatureCode)
            ? RestaurantProductTier.RestaurantPlus
            : RestaurantProductTier.Restaurant;
    }

    private static bool HasFeature(
        CommercialLicenseStatus status,
        string featureCode) =>
        status.Features?.Any(
            feature => string.Equals(
                feature?.Trim(),
                featureCode,
                StringComparison.OrdinalIgnoreCase)) == true;

}
