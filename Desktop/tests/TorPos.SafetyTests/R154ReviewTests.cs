using System.IO.Compression;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R154ReviewTests
{
    public static async Task Run(string root, Action<bool,string> assert)
    {
        var dir = Path.Combine(root, "r154-datev-kassenarchiv");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r154.db"));
        var settings = new FakeSettings();
        var audit = new FakeAudit();
        var management = new BusinessManagementService(db, settings, audit);
        var z = await management.CreateZArchiveAsync("r154", "TEST");

        var dsfinvk = new FakeDsfinvk();
        var tse = new FakeTse();
        var service = new DatevKassenarchivService(db, settings, dsfinvk, tse, audit);

        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT COUNT(*) FROM pragma_table_info('datev_kassenarchiv_outbox')
                WHERE name IN ('package_sha256','remote_archive_id','sent_at');
                """;
            assert(
                Convert.ToInt32(q.ExecuteScalar()) == 3,
                "R154 schema migration 22 creates the durable DATEV Kassenarchiv outbox columns");
        }

        var prepared = await service.PrepareZAsync(z, "r154");
        assert(
            prepared.PackageCreated &&
            prepared.Entry.State == "READY" &&
            File.Exists(prepared.Entry.PackagePath) &&
            prepared.Entry.PackageSha256.Length == 64,
            "R154 a completed Z close creates one immutable local DATEV package with SHA-256");

        assert(
            dsfinvk.LastFrom == z.CreatedAt.AddTicks(-1) &&
            dsfinvk.LastTo == z.CreatedAt,
            "R154 DATEV package selects exactly the target Z close instead of re-exporting the whole open period");

        using (var zip = ZipFile.OpenRead(prepared.Entry.PackagePath))
        {
            var names = zip.Entries.Select(x => x.FullName.Replace('\\','/')).ToArray();
            assert(
                names.Any(x => x.EndsWith("TOR-DATEV-MANIFEST.json", StringComparison.OrdinalIgnoreCase)) &&
                names.Any(x => x.Contains("DSFinV-K/", StringComparison.OrdinalIgnoreCase) && x.EndsWith("dummy.csv")) &&
                names.Any(x => x.StartsWith("TSE/", StringComparison.OrdinalIgnoreCase) && x.EndsWith(".tar")),
                "R154 DATEV package contains manifest, DSFinV-K payload and TSE TAR data");
        }

        var again = await service.PrepareZAsync(z, "r154");
        assert(
            !again.PackageCreated &&
            again.Entry.Id == prepared.Entry.Id &&
            again.Entry.PackagePath == prepared.Entry.PackagePath &&
            again.Entry.PackageSha256 == prepared.Entry.PackageSha256,
            "R154 retry reuses the same package and hash instead of generating a second version of the same Z");

        var waiting = await service.MarkWaitingForOfficialApiAsync(prepared.Entry.Id, "r154");
        var journal = await service.GetJournalAsync();
        assert(
            waiting.Contains("Developer-Portal", StringComparison.OrdinalIgnoreCase) &&
            journal.Single().State == "WAITING_API" &&
            journal.Single().AttemptCount == 1,
            "R154 transport stays fail-closed in WAITING_API until the official DATEV API/auth contract is configured");

        var report = await service.BuildJournalReportAsync();
        assert(
            report.Lines.Any(x => x.Contains($"Z {z.ZNumber:000000}", StringComparison.Ordinal)) &&
            report.Lines.Any(x => x.Contains("WAITING_API", StringComparison.Ordinal)),
            "R154 operator journal exposes the Z number and local DATEV transfer state");

        var deleteBlocked = false;
        try
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "DELETE FROM datev_kassenarchiv_outbox WHERE id=$id;";
            q.Parameters.AddWithValue("$id", prepared.Entry.Id);
            q.ExecuteNonQuery();
        }
        catch
        {
            deleteBlocked = true;
        }
        assert(
            deleteBlocked,
            "R154 database trigger prevents deleting the DATEV outbox audit trail");

        await File.AppendAllTextAsync(prepared.Entry.PackagePath, "tamper");
        var tamperBlocked = false;
        try
        {
            await service.MarkWaitingForOfficialApiAsync(prepared.Entry.Id, "r154");
        }
        catch (InvalidOperationException ex)
        {
            tamperBlocked = ex.Message.Contains("Hash", StringComparison.OrdinalIgnoreCase);
        }
        assert(
            tamperBlocked,
            "R154 a changed prepared package is rejected before any future DATEV transmission attempt");

        assert(
            audit.Events.Any(x => x == "DATEV_KASSENARCHIV_PREPARED") &&
            audit.Events.Any(x => x == "DATEV_KASSENARCHIV_WAITING_API"),
            "R154 preparation and transmission-wait state are recorded in the audit log");
    }

    private sealed class FakeSettings : ISettingsRepository
    {
        private readonly Dictionary<string,string> _values = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyDictionary<string,string>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string,string>>(_values);
        public Task<string> GetAsync(string key, string defaultValue = "", CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(key, defaultValue));
        public Task SaveManyAsync(IReadOnlyDictionary<string,string> values, CancellationToken ct = default)
        {
            foreach (var pair in values) _values[pair.Key] = pair.Value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudit : IAuditLog
    {
        public List<string> Events { get; } = new();
        public Task WriteAsync(string actor, string eventType, string entityType, string entityId, string details, CancellationToken ct = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }
        public Task<string> ExportCsvAsync(string targetPath, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
            Task.FromResult(targetPath);
    }

    private sealed class FakeDsfinvk : IDsfinvkExportService
    {
        public DateTimeOffset LastFrom { get; private set; }
        public DateTimeOffset LastTo { get; private set; }

        public Task<DsfinvkPreflightReport> ValidateAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            LastFrom = from;
            LastTo = to;
            return Task.FromResult(new DsfinvkPreflightReport(true, "2.4", Array.Empty<DsfinvkPreflightIssue>()));
        }

        public async Task<string> ExportAsync(DateTimeOffset from, DateTimeOffset to, string targetDirectory, CancellationToken ct = default)
        {
            LastFrom = from;
            LastTo = to;
            var folder = Path.Combine(targetDirectory, "DSFinV-K_FAKE");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "dummy.csv"), "Z_NR;TEST\n1;OK\n", ct);
            return folder;
        }
    }

    private sealed class FakeTse : ITseProvider
    {
        public string ProviderId => "fake";
        public string DisplayName => "Fake TSE";
        public string PreferredProduct => "Test";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, true, true, "", "");
        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();
        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new TseProbeResult(true, "OK"));
        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));
        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK"));
        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK"));
        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK"));
        public async Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default)
        {
            await File.WriteAllTextAsync(targetPath, "FAKE-TSE-TAR", ct);
            return new TseExportResult(true, "OK", targetPath);
        }
    }
}
