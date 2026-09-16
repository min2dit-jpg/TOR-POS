using System.Globalization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R132: reads the DSFinV-K master data and decides whether Vorgänge are
/// waiting for a closing. Shared by the settings guard, the Z-Bericht (which
/// stores the snapshot) and the export.
/// </summary>
public static class DsfinvkMasterDataStore
{
    public static string RunningSoftwareVersion => $"{TorRelease.Version} ({TorRelease.Revision})";

    public static async Task<DsfinvkMasterData> CurrentAsync(SqliteConnection c, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var q = Command(c, tx, "SELECT key,value FROM app_settings WHERE key IN ('company.name','company.street','company.zip','company.city','company.tax_no','company.vat_id',$version);"))
        {
            q.Parameters.AddWithValue("$version", DsfinvkMasterDataRules.SoftwareVersionKey);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                settings[r.GetString(0)] = r.GetString(1).Trim();
        }

        string serial = "", brand = "", model = "";
        await using (var q = Command(c, tx, "SELECT eas_serial,manufacturer,model FROM system_identity WHERE id=1;"))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                serial = r.GetString(0).Trim();
                brand = r.GetString(1).Trim();
                model = r.GetString(2).Trim();
            }
        }

        string Get(string key) => settings.TryGetValue(key, out var value) ? value : "";
        var version = Get(DsfinvkMasterDataRules.SoftwareVersionKey);

        return new DsfinvkMasterData(
            KasseId: serial,
            CompanyName: Get("company.name"),
            Street: Get("company.street"),
            Zip: Get("company.zip"),
            City: Get("company.city"),
            Country: "DEU",
            TaxNumber: Get("company.tax_no"),
            VatId: Get("company.vat_id").Replace(" ", ""),
            KasseBrand: brand,
            KasseModel: model,
            KasseSerial: serial,
            SoftwareBrand: TorRelease.Product,
            SoftwareVersion: version.Length > 0 ? version : RunningSoftwareVersion);
    }

    /// <summary>Master-data keys in <paramref name="values"/> whose (trimmed) value differs from the stored one.</summary>
    public static async Task<IReadOnlyList<string>> ChangedKeysAsync(SqliteConnection c, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var changed = new List<string>();
        foreach (var pair in values)
        {
            if (!DsfinvkMasterDataRules.IsMasterDataKey(pair.Key))
                continue;

            await using var q = Command(c, null, "SELECT value FROM app_settings WHERE key=$key;");
            q.Parameters.AddWithValue("$key", pair.Key);
            var stored = (await q.ExecuteScalarAsync(ct) as string ?? "").Trim();
            if (!string.Equals(stored, (pair.Value ?? "").Trim(), StringComparison.Ordinal))
                changed.Add(pair.Key);
        }

        return changed;
    }

    /// <summary>
    /// True when a fiscal Vorgang was recorded after the last Tagesabschluss:
    /// a sale, Storno or Retoure, a cash movement that is not a test entry, or
    /// an order handed to the TSE. Test-mode entries never reach a closing and
    /// do not count.
    /// </summary>
    public static async Task<bool> HasOpenVorgaengeAsync(SqliteConnection c, CancellationToken ct, SqliteTransaction? tx = null)
    {
        var from = "";
        await using (var q = Command(c, tx, "SELECT closed_at FROM daily_closings ORDER BY id DESC LIMIT 1;"))
        {
            if (await q.ExecuteScalarAsync(ct) is string closed && DateTimeOffset.TryParse(closed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                from = UtcText(at);
        }

        await using (var q = Command(c, tx, """
            SELECT (SELECT COUNT(*) FROM sales WHERE created_at_utc > $from)
                 + (SELECT COUNT(*) FROM cash_movements
                    WHERE created_at_utc > $from
                      AND movement_type IN ('EINLAGE','ENTNAHME')
                      AND fiscal_mode <> 'TEST_ONLY')
                 + (SELECT COUNT(*) FROM training_receipts WHERE created_at_utc > $from)
                 + (SELECT COUNT(*) FROM aborted_vorgaenge WHERE ended_at_utc > $from);
            """))
        {
            q.Parameters.AddWithValue("$from", from);
            if (Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0)
                return true;
        }

        await using (var q = Command(c, tx, """
            SELECT created_at FROM parked_receipts
            WHERE COALESCE(is_training,0)=0
              AND (tse_transaction_number<>'' OR tse_outage=1);
            """))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (DateTimeOffset.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) &&
                    string.CompareOrdinal(UtcText(created), from) > 0)
                    return true;
            }
        }

        return false;
    }

    public static string UtcText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        return q;
    }
}

/// <summary>
/// R132: DSFinV-K 3.2 - before master data change, a closing is created
/// automatically. Used by the settings screens instead of saving directly;
/// the guard in SettingsRepository refuses any other path.
/// </summary>
public sealed class DsfinvkMasterDataService
{
    public const string AutomaticClosingStatus = "AUTO_STAMMDATEN";

    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;
    private readonly BusinessManagementService _management;
    private readonly IDailyClosingGuard _closingGuard;
    private readonly IAuditLog _audit;

    public DsfinvkMasterDataService(
        SqliteDatabase db,
        ISettingsRepository settings,
        BusinessManagementService management,
        IDailyClosingGuard closingGuard,
        IAuditLog audit)
    {
        _db = db;
        _settings = settings;
        _management = management;
        _closingGuard = closingGuard;
        _audit = audit;
    }

    public sealed record SaveResult(ZArchiveRow? AutomaticClosing, IReadOnlyList<string> ChangedMasterData);

    /// <summary>
    /// Saves settings. When company master data changes while Vorgänge are
    /// waiting for a closing, the Z closing is created first - under the old
    /// master data - and the settings are saved afterwards. If a closing is not
    /// possible (open parked receipts), nothing is saved.
    /// </summary>
    public Task<SaveResult> SaveSettingsAsync(IReadOnlyDictionary<string, string> values, string actor, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            IReadOnlyList<string> changed;
            bool open;
            await using (var c = _db.OpenConnection())
            {
                changed = await DsfinvkMasterDataStore.ChangedKeysAsync(c, values, ct);
                open = changed.Count > 0 && await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, ct);
            }

            ZArchiveRow? closing = null;
            if (open)
            {
                var check = await _closingGuard.CheckAsync(ct);
                if (!check.Allowed)
                {
                    throw new InvalidOperationException(
                        $"{check.Message} Vor einer Änderung der Stammdaten ({string.Join(", ", changed)}) ist ein Kassenabschluss nötig (DSFinV-K 3.2). Die Einstellungen wurden nicht gespeichert.");
                }

                closing = await _management.CreateZArchiveAsync(actor, AutomaticClosingStatus, ct);
                await _audit.WriteAsync(actor, "Z_REPORT_AUTO_MASTER_DATA", "Z_REPORT", closing.ZNumber.ToString(CultureInfo.InvariantCulture),
                    $"DSFinV-K 3.2: Kassenabschluss vor Stammdatenänderung; geändert: {string.Join(", ", changed)}", ct);
            }

            await _settings.SaveManyAsync(values, ct);
            return new SaveResult(closing, changed);
        });

    /// <summary>
    /// At start-up: if TOR was updated while Vorgänge were waiting for a
    /// closing, close them under the version that recorded them (KASSE_SW_VERSION
    /// is Stammdaten, DSFinV-K 3.2.3). A closing at this point cannot be
    /// refused, because the change has already happened.
    /// </summary>
    public Task<ZArchiveRow?> EnsureSoftwareVersionAsync(string actor, CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            var stored = (await _settings.GetAsync(DsfinvkMasterDataRules.SoftwareVersionKey, "", ct)).Trim();
            var running = DsfinvkMasterDataStore.RunningSoftwareVersion;
            if (stored == running)
                return null;

            bool open;
            await using (var c = _db.OpenConnection())
                open = stored.Length > 0 && await DsfinvkMasterDataStore.HasOpenVorgaengeAsync(c, ct);

            if (!open)
            {
                await _settings.SaveManyAsync(new Dictionary<string, string> { [DsfinvkMasterDataRules.SoftwareVersionKey] = running }, ct);
                return (ZArchiveRow?)null;
            }

            var closing = await _management.CreateZArchiveAsync(actor, "AUTO_SOFTWAREUPDATE", ct);
            await _audit.WriteAsync(actor, "Z_REPORT_AUTO_SOFTWARE_UPDATE", "Z_REPORT", closing.ZNumber.ToString(CultureInfo.InvariantCulture),
                $"DSFinV-K 3.2: Kassenabschluss für Vorgänge unter {stored} vor Buchung unter {running}", ct);
            return closing;
        });
}
