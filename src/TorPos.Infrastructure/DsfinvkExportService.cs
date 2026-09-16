using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// DSFinV-K 2.4 export gate.
///
/// The official field order and index.xml definition are binding.
/// TOR therefore refuses to create a file set that merely "looks like"
/// DSFinV-K. This service validates whether the current database contains
/// the minimum data needed for a real export.
///
/// Export remains blocked until the full official descriptor/mapping and
/// Z-report/TSE transaction data are implemented and validated.
/// </summary>
public sealed class DsfinvkExportService : IDsfinvkExportService
{
    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;

    public DsfinvkExportService(
        SqliteDatabase db,
        ISettingsRepository settings)
    {
        _db = db;
        _settings = settings;
    }
public async Task<DsfinvkPreflightReport> ValidateAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
{
    return await IoQueue.RunAsync(async () =>
    {
        var issues = new List<DsfinvkPreflightIssue>();
        if (to < from)
        {
            issues.Add(new("RANGE", "Enddatum liegt vor dem Startdatum."));
        }

        var settings = await _settings.LoadAllAsync(ct);
        foreach (var key in new[]
        {
            "company.name",
            "company.street",
            "company.zip",
            "company.city",
            "cash.register.number"
        }

        )
        {
            if (string.IsNullOrWhiteSpace(settings.GetValueOrDefault(key)))
            {
                issues.Add(new("MASTER_DATA", $"Pflicht-Stammdatum fehlt: {key}"));
            }
        }

        await using var c = _db.OpenConnection();
        async Task<bool> TableExistsAsync(string table)
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type='table'
                  AND name=$name;
                """;
            q.Parameters.AddWithValue("$name", table);
            return Convert.ToInt32(await q.ExecuteScalarAsync(ct)) > 0;
        }

        foreach (var table in new[]
        {
            "sales",
            "sale_items",
            "cash_movements",
            "system_identity"
        }

        )
        {
            if (!await TableExistsAsync(table))
            {
                issues.Add(new("TABLE", $"Datenquelle fehlt: {table}"));
            }
        }

        // Required for a real DSFinV-K cashpoint closing module.
        if (!await TableExistsAsync("daily_closings"))
        {
            issues.Add(new("Z_CLOSING", "Produktive Z-/Kassenabschlussdaten fehlen noch."));
        }

        // Current sales only carry a generic fiscal_status and do not yet
        // persist the complete TSE response per completed transaction.
        if (!await TableExistsAsync("sale_fiscal_data"))
        {
            issues.Add(new("TSE_TRANSACTION_DATA", "Vollständige TSE-Transaktionsdaten je Bon werden noch nicht in sale_fiscal_data gespeichert."));
        }

        issues.Add(new("OFFICIAL_DESCRIPTOR", "Offizielle DSFinV-K-2.4 Feldreihenfolge/index.xml-Definition ist noch nicht als validierter TOR-Exportdescriptor eingebunden."));
        return new DsfinvkPreflightReport(Ready: issues.All(x => !x.Blocking), Version: "2.4", Issues: issues);
    });
}public async Task<string> ExportAsync(DateTimeOffset from, DateTimeOffset to, string targetDirectory, CancellationToken ct = default)
{
    return await IoQueue.RunAsync<string>(async () =>
    {
        var report = await ValidateAsync(from, to, ct);
        if (!report.Ready)
        {
            throw new InvalidOperationException("DSFinV-K Export gesperrt: " + string.Join(" | ", report.Issues.Where(x => x.Blocking).Select(x => x.Message)));
        }

        throw new NotSupportedException("DSFinV-K 2.4 Export ist noch nicht produktiv freigegeben. " + "TOR erzeugt absichtlich keinen unvollständigen Prüfdatensatz.");
    });
}}
