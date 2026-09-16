using System.Text.Json;

namespace TorPos.Core;

/// <summary>
/// R132: DSFinV-K 3.2 - "Zur Vermeidung von Redundanzen werden die Stammdaten
/// für jeden Kassenabschluss nur einmal gespeichert. Werden Änderungen an den im
/// Folgenden aufgeführten Stammdaten vorgenommen, ist zuvor automatisch ein
/// Abschluss zu erstellen." (similarly DSFinV-K 3: "vor einer Stammdaten-Änderung
/// [muss] ein Kassenabschluss erfolgen und erst anschließend wieder neu gebucht
/// werden").
///
/// TOR therefore stores the master data every closing was recorded under
/// (z_report_archive.master_data) and never lets them change while Vorgänge
/// are waiting for a closing.
/// </summary>
public static class DsfinvkMasterDataRules
{
    /// <summary>The settings behind Stamm_Abschluss / Stamm_Orte. Brand, model and
    /// serial of the till come from the immutable system identity.</summary>
    public static readonly IReadOnlyList<string> SettingKeys = new[]
    {
        "company.name",
        "company.street",
        "company.zip",
        "company.city",
        "company.tax_no",
        "company.vat_id",
    };

    /// <summary>
    /// The software version the Vorgänge of the open period were recorded
    /// under (KASSE_SW_VERSION). It moves to the running version only at a
    /// closing, so an update installed in the middle of a period is detected
    /// at the next start and closed under the version that recorded it.
    /// </summary>
    public const string SoftwareVersionKey = "dsfinvk.software_version";

    public static bool IsMasterDataKey(string key) =>
        SettingKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Serialize(DsfinvkMasterData master) => JsonSerializer.Serialize(master, Json);

    public static DsfinvkMasterData? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<DsfinvkMasterData>(json, Json);
    }
}

public sealed class MasterDataChangeRequiresClosingException : InvalidOperationException
{
    public MasterDataChangeRequiresClosingException(IEnumerable<string> keys)
        : base("Stammdaten (" + string.Join(", ", keys) + ") können erst nach einem Kassenabschluss geändert werden: " +
               "seit dem letzten Z-Bericht wurden Vorgänge erfasst (DSFinV-K 3.2).")
    {
    }
}
