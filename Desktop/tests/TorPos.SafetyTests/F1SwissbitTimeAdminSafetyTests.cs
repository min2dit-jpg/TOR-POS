using TorPos.Core;
using TorPos.Infrastructure;

public static class F1SwissbitTimeAdminSafetyTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        if (!OperatingSystem.IsWindows())
        {
            assert(
                true,
                "F-1 TimeAdmin DPAPI behavior test is Windows-only by design");
            return;
        }

        var dir = Path.Combine(root, "f1-timeadmin");
        Directory.CreateDirectory(dir);

        var db = await SafetyDatabase.CreateCurrentAsync(
            Path.Combine(dir, "timeadmin.db"));
        var settings = new SettingsRepository(db);

        var store = new TseTimeAdminPinStore(settings);
        await store.SaveAsync("54321");

        var rejectingBridge = new FakeSwissbitBridge
        {
            RejectNonEmptyPin = true,
            RemainingRetries = 1
        };
        var clock = new TseClockSafetyState();

        var provider = new SwissbitHardwareTseProvider(
            rejectingBridge,
            timeAdminPinStore: store,
            clockSafety: clock);

        var first = await provider.StartTransactionAsync(
            new TseTransactionStartRequest(
                "KASSE-F1",
                Array.Empty<byte>(),
                "Kassenbeleg-V1"));

        assert(
            rejectingBridge.NonEmptyPinAttempts == 1 &&
            first.TimeAdminPinRejected &&
            first.TimeAdminRemainingRetries == 1 &&
            first.Message.Contains(
                "automatisch gesperrt",
                StringComparison.OrdinalIgnoreCase) &&
            store.Suspended &&
            store.Current.Length == 0,
            "F-1 first failed stored TimeAdmin login suspends the PIN and exposes a cashier-facing warning");

        var second = await provider.StartTransactionAsync(
            new TseTransactionStartRequest(
                "KASSE-F1",
                Array.Empty<byte>(),
                "Kassenbeleg-V1"));

        assert(
            rejectingBridge.NonEmptyPinAttempts == 1 &&
            rejectingBridge.StartPins.Count == 2 &&
            rejectingBridge.StartPins[1].Length == 0,
            "F-1 suspended stored TimeAdmin PIN is not submitted a second time");

        var afterRestart =
            new TseTimeAdminPinStore(settings);
        await afterRestart.RefreshAsync();

        assert(
            afterRestart.Suspended &&
            afterRestart.RemainingRetries == 1 &&
            afterRestart.Current.Length == 0,
            "F-1 TimeAdmin suspension and remaining retries survive application restart");

        await settings.SaveManyAsync(
            new Dictionary<string, string>
            {
                [TseTimeAdminPinStore.SuspendedSetting] = "false",
                [TseTimeAdminPinStore.RemainingRetriesSetting] = "1"
            });

        var retryGuard =
            new TseTimeAdminPinStore(settings);
        await retryGuard.RefreshAsync();

        assert(
            !retryGuard.Suspended &&
            retryGuard.RemainingRetries == 1 &&
            retryGuard.HasStoredValue &&
            retryGuard.Current.Length == 0 &&
            !retryGuard.CanAutoUse,
            "F-1 retries <= 1 independently blocks automatic stored PIN use even if the suspension marker is absent");

        await retryGuard.SaveAsync("67890");

        assert(
            retryGuard.Current == "67890" &&
            retryGuard.RemainingRetries is null &&
            !retryGuard.Suspended,
            "F-1 explicit operator re-save is the only action that rearms stored TimeAdmin PIN use");

        var last =
            new DateTimeOffset(
                2026, 9, 24, 8, 0, 0,
                TimeSpan.Zero);

        var backwards =
            TseClockUpdatePolicy.Assess(
                last.AddSeconds(-1),
                last);

        var excessiveRuntimeDrift =
            TseClockUpdatePolicy.Assess(
                last.AddMinutes(10),
                last,
                last);

        var acceptableRuntimeDrift =
            TseClockUpdatePolicy.Assess(
                last.AddMinutes(4),
                last,
                last);

        assert(
            !backwards.Allowed &&
            !excessiveRuntimeDrift.Allowed &&
            acceptableRuntimeDrift.Allowed,
            "F-1 TSE clock policy rejects backward time and runtime drift above five minutes");

        var futureBound = new TseClockSafetyState();
        futureBound.Seed(DateTimeOffset.UtcNow.AddMinutes(1));

        var safeStore = new TseTimeAdminPinStore(settings);
        await safeStore.SaveAsync("67890");

        var clockBridge = new FakeSwissbitBridge();
        var clockProvider = new SwissbitHardwareTseProvider(
            clockBridge,
            timeAdminPinStore: safeStore,
            clockSafety: futureBound);

        var clockBlocked =
            await clockProvider.StartTransactionAsync(
                new TseTransactionStartRequest(
                    "KASSE-F1-CLOCK",
                    Array.Empty<byte>(),
                    "Kassenbeleg-V1"));

        assert(
            clockBridge.StartPins.Single().Length == 0 &&
            clockBlocked.Message.Contains(
                "Windows-Zeit",
                StringComparison.OrdinalIgnoreCase),
            "F-1 implausible host clock withholds TimeAdmin PIN before any possible TSE time write");
    }

    private sealed class FakeSwissbitBridge :
        ISwissbitSdkBridge
    {
        public bool RejectNonEmptyPin { get; init; }
        public int RemainingRetries { get; init; } = 2;
        public List<string> StartPins { get; } = new();
        public int NonEmptyPinAttempts { get; private set; }

        public bool IsAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() =>
            new(true, true, "TEST", "", "TEST");

        public Task<IReadOnlyList<string>> FindInstalledLibrariesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                Array.Empty<string>());

        public TseRuntimeStatus ConfigureLibrary(string libraryPath) =>
            GetRuntimeStatus();

        public Task<IReadOnlyList<TseDeviceInfo>> FindDevicesAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TseDeviceInfo>>(
                Array.Empty<TseDeviceInfo>());

        public Task<TseActivationResult> ActivateAsync(
            TseActivationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(
                new TseActivationResult(
                    true,
                    "TEST"));

        public Task<TseTransactionResult> StartTransactionAsync(
            TseTransactionStartRequest request,
            CancellationToken ct = default)
        {
            StartPins.Add(request.TimeAdminPin);

            if (request.TimeAdminPin.Length > 0)
            {
                NonEmptyPinAttempts++;

                if (RejectNonEmptyPin)
                {
                    return Task.FromResult(
                        new TseTransactionResult(
                            false,
                            "AUTH",
                            TimeAdminPinRejected: true,
                            TimeAdminRemainingRetries:
                                RemainingRetries));
                }
            }

            return Task.FromResult(
                new TseTransactionResult(
                    false,
                    "NO_TIME_SET"));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(
            TseTransactionUpdateRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(
                new TseTransactionResult(
                    false,
                    "TEST"));

        public Task<TseTransactionResult> FinishTransactionAsync(
            TseTransactionFinishRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(
                new TseTransactionResult(
                    false,
                    "TEST"));

        public Task<TseExportResult> ExportTarAsync(
            string targetPath,
            CancellationToken ct = default) =>
            Task.FromResult(
                new TseExportResult(
                    true,
                    "TEST",
                    targetPath));
    }
}
