using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record DatevKassenarchivOutboxEntry(
    long Id,
    long ZArchiveId,
    long ZNumber,
    DateTimeOffset CreatedAt,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    string State,
    string PackagePath,
    string PackageSha256,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    string LastError,
    string RemoteArchiveId,
    DateTimeOffset? SentAt);

public sealed record DatevKassenarchivPrepareResult(
    DatevKassenarchivOutboxEntry Entry,
    bool PackageCreated,
    string Message);

/// <summary>
/// R154 foundation for DATEV Datenservice Kassenarchiv.
///
/// The immutable local package is created immediately after a completed Z close.
/// The actual DATEV Online transport is intentionally NOT invented here:
/// endpoint/auth/client contract must come from the current DATEV Developer Portal
/// specification. Until a certified transport is plugged in, prepared packages
/// remain in WAITING_API state and can be retried without being regenerated.
/// </summary>
public sealed class DatevKassenarchivService
{
    public const string SettingEnabled = "datev.kassenarchiv.enabled";
    public const string SettingAutoAfterZ = "datev.kassenarchiv.auto_after_z";

    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;
    private readonly IDsfinvkExportService _dsfinvk;
    private readonly ITseProvider _tse;
    private readonly IAuditLog _audit;

    public DatevKassenarchivService(
        SqliteDatabase db,
        ISettingsRepository settings,
        IDsfinvkExportService dsfinvk,
        ITseProvider tse,
        IAuditLog audit)
    {
        _db = db;
        _settings = settings;
        _dsfinvk = dsfinvk;
        _tse = tse;
        _audit = audit;
    }

    public string OutboxDirectory
    {
        get
        {
            var parent = Path.GetDirectoryName(_db.DatabasePath);
            if (string.IsNullOrWhiteSpace(parent))
                parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(parent!, "DATEV-Kassenarchiv-Outbox");
        }
    }

    public async Task<DatevKassenarchivPrepareResult> PrepareZAsync(
        ZArchiveRow z,
        string actor,
        CancellationToken ct = default)
    {
        var existing = await FindByZAsync(z.Id, ct);
        if (existing is not null &&
            !string.IsNullOrWhiteSpace(existing.PackagePath) &&
            File.Exists(existing.PackagePath) &&
            !string.IsNullOrWhiteSpace(existing.PackageSha256))
        {
            VerifyPackageHash(existing);
            return new(existing, false, "Vorhandenes unveränderbares DATEV-Paket wird wiederverwendet.");
        }

        var rowId = existing?.Id ?? await InsertPreparingAsync(z, ct);
        Directory.CreateDirectory(OutboxDirectory);

        var token = $"Z{z.ZNumber:000000}-{z.PeriodTo:yyyyMMdd-HHmmss}";
        var work = Path.Combine(OutboxDirectory, token + ".working");
        var package = Path.Combine(OutboxDirectory, token + ".zip");

        try
        {
            if (Directory.Exists(work))
                Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            if (File.Exists(package))
                throw new InvalidOperationException(
                    $"DATEV-Paketdatei existiert bereits, aber Outbox-Hash fehlt: {package}");

            var preflight = await _dsfinvk.ValidateAsync(z.PeriodFrom, z.PeriodTo, ct);
            if (!preflight.Ready)
                throw new InvalidOperationException(
                    "DSFinV-K für diesen Z-Abschluss ist nicht exportbereit: " +
                    string.Join(" | ", preflight.Issues.Where(x => x.Blocking).Select(x => x.Message)));

            var dsfinvkRoot = Path.Combine(work, "DSFinV-K");
            Directory.CreateDirectory(dsfinvkRoot);
            await _dsfinvk.ExportAsync(z.PeriodFrom, z.PeriodTo, dsfinvkRoot, ct);

            if (!_tse.ExportAvailable)
                throw new InvalidOperationException(
                    "TSE-TAR-Export ist nicht verfügbar. DATEV-Paket bleibt offen; Z-Abschluss selbst ist davon nicht betroffen.");

            var tseDir = Path.Combine(work, "TSE");
            Directory.CreateDirectory(tseDir);
            var tsePath = Path.Combine(tseDir, $"TSE-Z{z.ZNumber:000000}.tar");
            var tseResult = await _tse.ExportTarAsync(tsePath, ct);
            if (!tseResult.Success || !File.Exists(tsePath))
                throw new InvalidOperationException(
                    "TSE-TAR-Export fehlgeschlagen: " + tseResult.Message);

            var manifestPath = Path.Combine(work, "TOR-DATEV-MANIFEST.json");
            var fileEntries = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories)
                .Where(x => !string.Equals(x, manifestPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    path = Path.GetRelativePath(work, x).Replace('\\', '/'),
                    bytes = new FileInfo(x).Length,
                    sha256 = Sha256(x)
                })
                .ToArray();

            var manifest = new
            {
                schema = "TOR-DATEV-KASSENARCHIV-1",
                createdAt = DateTimeOffset.Now,
                zArchiveId = z.Id,
                zNumber = z.ZNumber,
                periodFrom = z.PeriodFrom,
                periodTo = z.PeriodTo,
                receiptCount = z.ReceiptCount,
                grossCents = z.GrossCents,
                fiscalStatus = z.FiscalStatus,
                dsfinvk = "2.4",
                tseIncluded = true,
                files = fileEntries
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                ct);

            ZipFile.CreateFromDirectory(
                work,
                package,
                CompressionLevel.Optimal,
                includeBaseDirectory: false);

            var sha = Sha256(package);
            await MarkReadyAsync(rowId, package, sha, ct);

            try { Directory.Delete(work, true); } catch { }

            var ready = await GetByIdAsync(rowId, ct)
                ?? throw new InvalidOperationException("DATEV-Outbox-Eintrag konnte nicht erneut geladen werden.");

            await _audit.WriteAsync(
                actor,
                "DATEV_KASSENARCHIV_PREPARED",
                "Z_REPORT",
                z.ZNumber.ToString(),
                $"outbox_id={ready.Id}; sha256={ready.PackageSha256}; period={z.PeriodFrom:O}..{z.PeriodTo:O}",
                ct);

            return new(
                ready,
                true,
                "DATEV-Paket vorbereitet. Online-Transport wartet auf die offizielle DATEV API-Konfiguration.");
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            await MarkPreparationFailedAsync(rowId, ex.Message, ct);
            await _audit.WriteAsync(
                actor,
                "DATEV_KASSENARCHIV_PREPARE_FAILED",
                "Z_REPORT",
                z.ZNumber.ToString(),
                ex.Message,
                ct);
            throw;
        }
    }

    public async Task<DatevKassenarchivOutboxEntry?> FindByZAsync(
        long zArchiveId,
        CancellationToken ct = default)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,period_from,period_to,state,
                   package_path,package_sha256,attempt_count,last_attempt_at,last_error,
                   remote_archive_id,sent_at
            FROM datev_kassenarchiv_outbox
            WHERE z_archive_id=$z;
            """;
        q.Parameters.AddWithValue("$z", zArchiveId);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<DatevKassenarchivOutboxEntry>> GetJournalAsync(
        int max = 200,
        CancellationToken ct = default)
    {
        max = Math.Clamp(max, 1, 1000);
        var rows = new List<DatevKassenarchivOutboxEntry>();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,period_from,period_to,state,
                   package_path,package_sha256,attempt_count,last_attempt_at,last_error,
                   remote_archive_id,sent_at
            FROM datev_kassenarchiv_outbox
            ORDER BY z_number DESC
            LIMIT $max;
            """;
        q.Parameters.AddWithValue("$max", max);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(Read(r));
        return rows;
    }

    public async Task<ReportDocument> BuildJournalReportAsync(CancellationToken ct = default)
    {
        var rows = await GetJournalAsync(200, ct);
        var lines = new List<string>
        {
            "DATEV Kassenarchiv · Übertragungsjournal",
            "Lokale Vorbereitung ist aktiv. Online-Transport wird erst nach Implementierung der offiziellen DATEV Developer-Portal API aktiviert.",
            "",
            "Z-Nr. | Zeitraum | Status | Versuche | Paket / Fehler"
        };
        lines.AddRange(rows.Select(x =>
            $"Z {x.ZNumber:000000} | {x.PeriodFrom:dd.MM. HH:mm}–{x.PeriodTo:dd.MM. HH:mm} | " +
            $"{x.State} | {x.AttemptCount} | " +
            $"{(!string.IsNullOrWhiteSpace(x.LastError) ? x.LastError : Path.GetFileName(x.PackagePath))}"));
        if (rows.Count == 0)
            lines.Add("Noch keine DATEV-Pakete vorbereitet.");
        return new ReportDocument("DATEV KASSENARCHIV · JOURNAL", lines, DateTimeOffset.Now);
    }

    public async Task<string> MarkWaitingForOfficialApiAsync(
        long outboxId,
        string actor,
        CancellationToken ct = default)
    {
        var row = await GetByIdAsync(outboxId, ct)
            ?? throw new InvalidOperationException("DATEV-Outbox-Eintrag nicht gefunden.");
        VerifyPackageHash(row);

        var now = DateTimeOffset.Now;
        const string message =
            "Online-Transport noch nicht freigeschaltet: offizielle DATEV Developer-Portal API/Auth-Konfiguration erforderlich.";

        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE datev_kassenarchiv_outbox
            SET state='WAITING_API',
                attempt_count=attempt_count+1,
                last_attempt_at=$at,
                last_error=$error
            WHERE id=$id AND state<>'SENT';
            """;
        q.Parameters.AddWithValue("$at", now.ToString("O"));
        q.Parameters.AddWithValue("$error", message);
        q.Parameters.AddWithValue("$id", outboxId);
        await q.ExecuteNonQueryAsync(ct);

        await _audit.WriteAsync(
            actor,
            "DATEV_KASSENARCHIV_WAITING_API",
            "DATEV_OUTBOX",
            outboxId.ToString(),
            $"sha256={row.PackageSha256}",
            ct);

        return message;
    }

    private async Task<long> InsertPreparingAsync(ZArchiveRow z, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO datev_kassenarchiv_outbox(
              z_archive_id,z_number,created_at,period_from,period_to,state)
            VALUES($za,$zn,$created,$from,$to,'PREPARING')
            ON CONFLICT(z_archive_id) DO UPDATE SET
              state=CASE
                WHEN datev_kassenarchiv_outbox.package_sha256<>'' THEN datev_kassenarchiv_outbox.state
                ELSE 'PREPARING'
              END;
            SELECT id FROM datev_kassenarchiv_outbox WHERE z_archive_id=$za;
            """;
        q.Parameters.AddWithValue("$za", z.Id);
        q.Parameters.AddWithValue("$zn", z.ZNumber);
        q.Parameters.AddWithValue("$created", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$from", z.PeriodFrom.ToString("O"));
        q.Parameters.AddWithValue("$to", z.PeriodTo.ToString("O"));
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    private async Task MarkReadyAsync(long id, string path, string sha, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE datev_kassenarchiv_outbox
            SET state='READY',
                package_path=$path,
                package_sha256=$sha,
                last_error=''
            WHERE id=$id AND package_sha256='';
            """;
        q.Parameters.AddWithValue("$path", path);
        q.Parameters.AddWithValue("$sha", sha);
        q.Parameters.AddWithValue("$id", id);
        var changed = await q.ExecuteNonQueryAsync(ct);
        if (changed != 1)
            throw new InvalidOperationException("DATEV-Paket konnte nicht atomar als READY gespeichert werden.");
    }

    private async Task MarkPreparationFailedAsync(long id, string error, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            UPDATE datev_kassenarchiv_outbox
            SET state=CASE WHEN package_sha256='' THEN 'PREPARE_FAILED' ELSE state END,
                last_error=CASE WHEN package_sha256='' THEN $error ELSE last_error END
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$error", error);
        q.Parameters.AddWithValue("$id", id);
        await q.ExecuteNonQueryAsync(ct);
    }

    private async Task<DatevKassenarchivOutboxEntry?> GetByIdAsync(long id, CancellationToken ct)
    {
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,z_archive_id,z_number,created_at,period_from,period_to,state,
                   package_path,package_sha256,attempt_count,last_attempt_at,last_error,
                   remote_archive_id,sent_at
            FROM datev_kassenarchiv_outbox WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static DatevKassenarchivOutboxEntry Read(SqliteDataReader r) =>
        new(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetInt64(2),
            DateTimeOffset.Parse(r.GetString(3)),
            DateTimeOffset.Parse(r.GetString(4)),
            DateTimeOffset.Parse(r.GetString(5)),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetInt32(9),
            r.IsDBNull(10) || string.IsNullOrWhiteSpace(r.GetString(10)) ? null : DateTimeOffset.Parse(r.GetString(10)),
            r.GetString(11),
            r.GetString(12),
            r.IsDBNull(13) || string.IsNullOrWhiteSpace(r.GetString(13)) ? null : DateTimeOffset.Parse(r.GetString(13)));

    private static void VerifyPackageHash(DatevKassenarchivOutboxEntry row)
    {
        if (string.IsNullOrWhiteSpace(row.PackagePath) || !File.Exists(row.PackagePath))
            throw new InvalidOperationException("DATEV-Paketdatei fehlt. Ein neues Paket wird nicht stillschweigend über das alte geschrieben.");

        var actual = Sha256(row.PackagePath);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(row.PackageSha256)))
            throw new InvalidOperationException(
                "DATEV-Paket-Hash stimmt nicht mehr. Paket wurde nach Vorbereitung verändert; Übertragung ist gesperrt.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
