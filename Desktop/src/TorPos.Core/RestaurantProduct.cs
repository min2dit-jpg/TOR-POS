using System.Collections.Frozen;

namespace TorPos.Core;

/// <summary>
/// Commercial product tier for the dedicated TOR Restaurant line.
/// The initial TOR Restaurant generation is German-only.
/// This type deliberately has no dependency on the multilingual UI layer.
/// </summary>
public enum RestaurantProductTier
{
    Restaurant = 1,
    RestaurantPlus = 2
}

public enum RestaurantFeature
{
    Tischplan,
    TischOeffnen,
    TischUmbuchen,
    TischeZusammenlegen,
    TischRechnung,
    SplitrechnungNachArtikel,
    SplitrechnungNachPerson,
    KellnerKonten,
    KellnerRechte,
    OffeneTische,
    GrundlegendesKuechenrouting,
    Kuechendrucker,
    AbholUndAusserHaus,

    HandheldBestellung,
    KitchenDisplaySystem,
    ErweiterteKuechenstationen,
    Gangsteuerung,
    Reservierungen,
    Kundenkartei,
    Kundenbindung,
    MehrereKassen,
    Filialverbund,
    ErweiterteRestaurantAuswertung
}

/// <summary>
/// One authoritative feature matrix prevents Standard/Plus drift across
/// desktop UI, licensing, setup and later handheld/KDS services.
/// </summary>
public static class RestaurantProductFeatures
{
    private static readonly FrozenSet<RestaurantFeature> Standard =
        new[]
        {
            RestaurantFeature.Tischplan,
            RestaurantFeature.TischOeffnen,
            RestaurantFeature.TischUmbuchen,
            RestaurantFeature.TischeZusammenlegen,
            RestaurantFeature.TischRechnung,
            RestaurantFeature.SplitrechnungNachArtikel,
            RestaurantFeature.SplitrechnungNachPerson,
            RestaurantFeature.KellnerKonten,
            RestaurantFeature.KellnerRechte,
            RestaurantFeature.OffeneTische,
            RestaurantFeature.GrundlegendesKuechenrouting,
            RestaurantFeature.Kuechendrucker,
            RestaurantFeature.AbholUndAusserHaus
        }.ToFrozenSet();

    private static readonly FrozenSet<RestaurantFeature> PlusOnly =
        new[]
        {
            RestaurantFeature.HandheldBestellung,
            RestaurantFeature.KitchenDisplaySystem,
            RestaurantFeature.ErweiterteKuechenstationen,
            RestaurantFeature.Gangsteuerung,
            RestaurantFeature.Reservierungen,
            RestaurantFeature.Kundenkartei,
            RestaurantFeature.Kundenbindung,
            RestaurantFeature.MehrereKassen,
            RestaurantFeature.Filialverbund,
            RestaurantFeature.ErweiterteRestaurantAuswertung
        }.ToFrozenSet();

    public static bool Includes(
        RestaurantProductTier tier,
        RestaurantFeature feature) =>
        Standard.Contains(feature) ||
        (tier == RestaurantProductTier.RestaurantPlus && PlusOnly.Contains(feature));

    public static IReadOnlyCollection<RestaurantFeature> FeaturesFor(
        RestaurantProductTier tier) =>
        Enum.GetValues<RestaurantFeature>()
            .Where(feature => Includes(tier, feature))
            .ToArray();

    public static bool IsPlusOnly(RestaurantFeature feature) =>
        PlusOnly.Contains(feature);
}
