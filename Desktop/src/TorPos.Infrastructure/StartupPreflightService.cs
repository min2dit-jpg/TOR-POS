using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record StartupPreflightResult(
    bool DatabaseHealthy,
    IReadOnlyList<string> Warnings);

public sealed class StartupPreflightService
{
    private readonly SqliteDatabase _db;
    private readonly ITseProvider _tse;

    public StartupPreflightService(
        SqliteDatabase db,
        ITseProvider tse)
    {
        _db = db;
        _tse = tse;
    }

    public async Task<StartupPreflightResult> RunAsync(
        CancellationToken ct = default)
    {
        var warnings =
            new List<string>();

        await using var c =
            _db.OpenConnection();

        await using (var q = c.CreateCommand())
        {
            q.CommandText = "PRAGMA quick_check;";

            var result =
                Convert.ToString(
                    await q.ExecuteScalarAsync(ct)) ??
                "";

            if (!string.Equals(
                result,
                "ok",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "SQLite quick_check fehlgeschlagen: " +
                    result);
            }
        }

        try
        {
            var runtime =
                _tse.GetRuntimeStatus();

            if (!runtime.SdkLoaded)
            {
                warnings.Add(
                    "Swissbit SDK nicht geladen: " +
                    runtime.Message);
            }
            else if (!runtime.RequiredApiAvailable)
            {
                warnings.Add(
                    "Swissbit SDK geladen, API aber unvollständig: " +
                    runtime.Message);
            }
        }
        catch (Exception ex)
        {
            warnings.Add(
                "TSE-Runtimeprüfung: " +
                ex.Message);
        }

        return new StartupPreflightResult(
            true,
            warnings);
    }
}
