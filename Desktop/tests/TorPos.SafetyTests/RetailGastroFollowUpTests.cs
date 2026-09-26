using TorPos.Core;
using TorPos.Infrastructure;

// Einzelhandel/Gastro follow-up to the 24.09. review: V-3 (a DSFinV-K export
// no longer holds the single-writer queue, so the till keeps selling) and O-3
// (a Ready TSE probe is reused for a short time instead of re-probing the TSE
// before every transaction, without weakening any failure path).
public static partial class RetailGastroFollowUpTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "retail-gastro-follow-up");
        Directory.CreateDirectory(dir);

        await ExportDoesNotHoldQueue(dir, assert);
        await ProbeCache(dir, assert);
        ReceiptWiring(assert);
    }

    private static void ReceiptWiring(Action<bool, string> assert)
    {
        assert(
            ReceiptFiscalStates.Of(false, "17", "c2ln") == ReceiptFiscalState.Signed &&
            ReceiptFiscalStates.Of(true, "", "") == ReceiptFiscalState.DocumentedOutage &&
            ReceiptFiscalStates.Of(true, "17", "c2ln") == ReceiptFiscalState.DocumentedOutage &&
            ReceiptFiscalStates.Of(false, "17", "") == ReceiptFiscalState.NotCompleted &&
            ReceiptFiscalStates.Of(false, "", null) == ReceiptFiscalState.NotCompleted,
            "Receipt policy: a sale is presentable only when signed (number and signature) or its TSE outage is documented");

        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var state = main.IndexOf("ReceiptFiscalStates.Of(sale.TseOutage, sale.TseTransactionNumber, sale.TseSignature)", StringComparison.Ordinal);
        var plan = main.IndexOf("ReceiptDeliveryPolicy.Plan(", state < 0 ? 0 : state, StringComparison.Ordinal);
        var withheld = main.IndexOf("await WithholdReceiptNotFiscalAsync(sale);", state < 0 ? 0 : state, StringComparison.Ordinal);
        var offer = main.IndexOf("else if(receiptPlan.Offered.Contains(ReceiptDeliveryChannel.QrCode))", state < 0 ? 0 : state, StringComparison.Ordinal);
        assert(
            state > 0 && plan > state && withheld > state && offer > withheld &&
            main.Contains("GermanFiscalRulesets.Resolve(DateOnly.FromDateTime(DateTime.Now))", StringComparison.Ordinal) &&
            main.Contains("\"RECEIPT_WITHHELD_NOT_FISCAL\"", StringComparison.Ordinal),
            "Receipt policy: the Einzelhandel/Gastro checkout decides paper/QR through the rule set in force and withholds - with a German message and audit entry - a receipt for a sale that is neither signed nor a documented outage");

        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var window = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/TseChangeJournalWindow.cs"));
        assert(
            settings.Contains("TseChangeJournalWindow.ForDatabase(_currentUser.Username, _currentUser.IsAdmin)", StringComparison.Ordinal) &&
            window.Contains("IsEnabled = canEdit", StringComparison.Ordinal) &&
            window.Contains("if (!_canEdit ||", StringComparison.Ordinal) &&
            window.Contains("SetNotificationStatusAsync(", StringComparison.Ordinal) &&
            !window.Contains("RecordAsync(", StringComparison.Ordinal),
            "TSE-Wechselprotokoll: the settings page opens the journal; only an administrator records the notification status, and the window never writes a TSE change itself");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }

    private static async Task ExportDoesNotHoldQueue(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "v3.db"));
        var export = new DsfinvkExportService(db, new SettingsRepository(db));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var range = DsfinvkExportRange.ForDates(today.AddDays(-30), today);

        // Hold the single-writer queue, as a long checkout write would. Before
        // V-3 the export queued behind it (and everything queued behind the export).
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = IoQueue.RunAsync(async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await entered.Task;

        var validate = export.ValidateAsync(range.FromInclusive, range.ToInclusive);
        var finished = await Task.WhenAny(validate, Task.Delay(TimeSpan.FromSeconds(20))) == validate;
        release.TrySetResult();
        await blocker;
        var report = await validate;

        assert(finished && report.Issues.Any(x => x.Code == "NO_CLOSING"),
            "V-3 the DSFinV-K preflight/export plan runs on its own read snapshot while the write queue is busy - checkout is never blocked by an export");
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void Advance(TimeSpan by) => Ticks += by.Ticks;
    }

    private sealed class CountingTse : ITseProvider
    {
        public int Probes;
        public int Starts;
        public TseConnectionState State = TseConnectionState.Ready;
        public bool FailStart;
        public DateTimeOffset CertificateExpires = DateTimeOffset.UtcNow.AddYears(2);

        public string ProviderId => "FAKE";
        public string DisplayName => "Counting TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;
        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");
        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default)
        {
            Probes++;
            return Task.FromResult(new TseProbeResult(State, State.ToString(),
                new TseDeviceInfo("Fake", "Fake", "", "", "SER-1", "", null, "", CertificateExpiresAtUtc: CertificateExpires)));
        }

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default)
        {
            Starts++;
            return Task.FromResult(FailStart
                ? new TseTransactionResult(false, "TSE entfernt")
                : new TseTransactionResult(true, "OK", (ulong)Starts, 1, DateTimeOffset.UtcNow, "SER-1", "c2ln"));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "SER-1", "c2ln"));
        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(true, "OK", targetPath));
    }

    private static async Task ProbeCache(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "o3.db"));
        var audit = new AuditLogRepository(db);
        var outages = new TseOutageRepository(db, audit);
        var tse = new CountingTse();
        var time = new ManualTime();
        var failSafe = new TseFailSafeService(tse, outages, audit, time);
        var request = new TseTransactionStartRequest("CLIENT", Array.Empty<byte>(), "Kassenbeleg-V1");

        var first = await failSafe.StartTransactionAsync(request, "kasse");
        time.Advance(TimeSpan.FromSeconds(10));
        var second = await failSafe.StartTransactionAsync(request, "kasse");
        var probesWithinWindow = tse.Probes;
        time.Advance(TimeSpan.FromSeconds(25));
        await failSafe.StartTransactionAsync(request, "kasse");
        assert(
            first.Result.Success && !first.TseOutage && second.Result.Success &&
            probesWithinWindow == 1 && tse.Probes == 2 && tse.Starts == 3,
            "O-3 a Ready TSE probe is reused for 30 seconds, then the TSE is probed again");

        tse.FailStart = true;
        var removed = await failSafe.StartTransactionAsync(request, "kasse");
        tse.FailStart = false;
        tse.State = TseConnectionState.NotFound;
        var afterFailure = await failSafe.StartTransactionAsync(request, "kasse");
        assert(
            !removed.Result.Success && removed.TseOutage &&
            !afterFailure.Result.Success && afterFailure.TseOutage && tse.Probes == 3 &&
            await outages.GetOpenAsync() is not null,
            "O-3 a failed transaction clears the cached probe: the next one probes again and a missing TSE is an outage, never a signed sale");

        tse.State = TseConnectionState.Ready;
        await failSafe.ProbeAsync();
        var probes = tse.Probes;
        tse.CertificateExpires = DateTimeOffset.UtcNow.AddMinutes(-1);
        await failSafe.ProbeAsync();
        var expired = await failSafe.StartTransactionAsync(request, "kasse");
        assert(
            !expired.Result.Success && expired.TseOutage && tse.Probes == probes + 2,
            "O-3 an explicit probe that finds an expired certificate is never hidden by the cache");
    }
}
