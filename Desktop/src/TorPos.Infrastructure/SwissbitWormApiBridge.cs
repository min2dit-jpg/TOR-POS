using System.Runtime.InteropServices;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Native bridge for the Swissbit WORM API used by Hardware-TSE devices.
///
/// TOR does not redistribute Swissbit SDK binaries. The bridge loads an
/// officially obtained WormAPI.dll at runtime from the application folder
/// or from the TOR SwissbitSdk subfolder.
///
/// All calls are serialized because the vendor API must not be accessed
/// concurrently from multiple TOR threads.
///
/// IMPORTANT:
/// The production release remains blocked until the exact SDK package used
/// for Hardware TSE 2 has been checked against Swissbit's official SDK/API
/// documentation and real hardware acceptance tests have passed.
/// </summary>
public sealed class SwissbitWormApiBridge : ISwissbitSdkBridge, IDisposable
{
    private const int WormOk = 0;
    private const int WormErrorNoCard = 2;
    private const int WormErrorClientNotRegistered = 0x1011;
    private const int WormErrorNeedsSelfTest = 0x1054;
    private const int WormErrorNeedsSelfTestPassed = 0x1055;

    private const uint InitUninitialized = 0;
    private const uint InitInitialized = 1;
    private const uint InitDecommissioned = 2;

    private const int UserAdmin = 1;
    private const int UserTimeAdmin = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadSync = new();

    private IntPtr _library;
    private string _libraryPath = "";
    private string _loadError = "";
    private string _sdkVersion = "";
    private Dictionary<string, IntPtr> _exports =
        new(StringComparer.Ordinal);

    private static string ConfiguredLibraryPathFile =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "TOR-POS-Pro",
            "swissbit-sdk-path.txt");

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _library != IntPtr.Zero && RequiredApiAvailable;
        }
    }

    public bool ActivationAvailable
    {
        get
        {
            EnsureLoaded();
            return IsAvailable &&
                   Has("worm_tse_setup") &&
                   Has("worm_tse_runSelfTest") &&
                   Has("worm_user_login") &&
                   Has("worm_user_logout") &&
                   Has("worm_tse_registerClient") &&
                   (Has("worm_tse_ctss_enable") || Has("worm_tse_ers_enable")) &&
                   Has("worm_tse_updateTime");
        }
    }

    public bool TransactionAvailable
    {
        get
        {
            EnsureLoaded();
            return IsAvailable &&
                   Has("worm_transaction_response_new") &&
                   Has("worm_transaction_response_free") &&
                   Has("worm_transaction_start") &&
                   Has("worm_transaction_update") &&
                   Has("worm_transaction_finish") &&
                   Has("worm_transaction_response_transactionNumber") &&
                   Has("worm_transaction_response_signatureCounter") &&
                   Has("worm_transaction_response_logTime") &&
                   Has("worm_transaction_response_serialNumber") &&
                   Has("worm_transaction_response_signature");
        }
    }

    public bool ExportAvailable
    {
        get
        {
            EnsureLoaded();
            return IsAvailable && Has("worm_export_tar");
        }
    }

    private bool RequiredApiAvailable =>
        Has("worm_init") &&
        Has("worm_cleanup") &&
        Has("worm_getVersion") &&
        Has("worm_info_new") &&
        Has("worm_info_read") &&
        Has("worm_info_free") &&
        Has("worm_info_tseSerialNumber") &&
        Has("worm_info_formFactor") &&
        Has("worm_info_certificateExpirationDate") &&
        Has("worm_info_initializationState");

    public TseRuntimeStatus GetRuntimeStatus()
    {
        EnsureLoaded();

        if (_library == IntPtr.Zero)
        {
            return new TseRuntimeStatus(
                false,
                false,
                "",
                _libraryPath,
                string.IsNullOrWhiteSpace(_loadError)
                    ? "WormAPI.dll wurde nicht gefunden."
                    : _loadError);
        }

        return new TseRuntimeStatus(
            true,
            RequiredApiAvailable,
            _sdkVersion,
            _libraryPath,
            RequiredApiAvailable
                ? "Swissbit WORM API geladen."
                : "Swissbit DLL wurde geladen, aber benötigte API-Exporte fehlen.");
    }


    public async Task<IReadOnlyList<string>> FindInstalledLibrariesAsync(
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        return await Task.Run(() =>
        {
            var found = new List<string>();

            foreach (var candidate in CandidateLibraries())
            {
                ct.ThrowIfCancellationRequested();

                if (Path.IsPathRooted(candidate) &&
                    File.Exists(candidate) &&
                    IsCompatibleLibrary(candidate))
                {
                    found.Add(Path.GetFullPath(candidate));
                }
            }

            foreach (var root in SearchRoots())
            {
                ct.ThrowIfCancellationRequested();

                foreach (var candidate in EnumerateFilesBounded(
                    root,
                    "WormAPI.dll",
                    maxDepth: 6,
                    maxResults: 30,
                    ct))
                {
                    if (IsCompatibleLibrary(candidate))
                        found.Add(Path.GetFullPath(candidate));

                    if (found.Count >= 30)
                        break;
                }

                if (found.Count >= 30)
                    break;
            }

            return (IReadOnlyList<string>)found
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, ct);
    }

    public TseRuntimeStatus ConfigureLibrary(
        string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new TseRuntimeStatus(
                false, false, "", "",
                "Keine WormAPI.dll ausgewählt.");
        }

        libraryPath = Path.GetFullPath(libraryPath);

        if (!File.Exists(libraryPath))
        {
            return new TseRuntimeStatus(
                false, false, "", libraryPath,
                "Die ausgewählte Datei existiert nicht.");
        }

        if (!string.Equals(
            Path.GetFileName(libraryPath),
            "WormAPI.dll",
            StringComparison.OrdinalIgnoreCase))
        {
            return new TseRuntimeStatus(
                false, false, "", libraryPath,
                "Bitte die offizielle Swissbit WormAPI.dll auswählen.");
        }

        if (!IsCompatibleLibrary(libraryPath))
        {
            return new TseRuntimeStatus(
                false, false, "", libraryPath,
                "Die DLL enthält nicht die benötigte Swissbit WORM API.");
        }

        lock (_loadSync)
        {
            ResetLoadedLibrary();

            try
            {
                var configDir =
                    Path.GetDirectoryName(
                        ConfiguredLibraryPathFile)!;

                Directory.CreateDirectory(configDir);

                File.WriteAllText(
                    ConfiguredLibraryPathFile,
                    libraryPath);
            }
            catch (Exception ex)
            {
                _loadError =
                    "SDK-Pfad konnte nicht gespeichert werden: " +
                    ex.Message;

                return GetRuntimeStatus();
            }
        }

        EnsureLoaded();
        return GetRuntimeStatus();
    }

    public async Task<IReadOnlyList<TseDeviceInfo>> FindDevicesAsync(
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!IsAvailable)
            return Array.Empty<TseDeviceInfo>();

        return await RunExclusiveAsync(() =>
        {
            var result = new List<TseDeviceInfo>();

            foreach (var mountPoint in FindCandidateMountPoints())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var session = OpenSession(mountPoint);
                    result.Add(ReadDeviceInfo(session, mountPoint));
                }
                catch
                {
                    // One bad drive must not prevent detection of another real TSE.
                }
            }

            return (IReadOnlyList<TseDeviceInfo>)result;
        }, ct);
    }

    public async Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!ActivationAvailable)
        {
            return new TseActivationResult(
                false,
                "Swissbit WORM API für Aktivierung ist nicht vollständig verfügbar.");
        }

        var validation = ValidateActivationRequest(request);
        if (validation is not null)
            return new TseActivationResult(false, validation);

        return await RunExclusiveAsync(() =>
        {
            var mount = FindCandidateMountPoints().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(mount))
            {
                return new TseActivationResult(
                    false,
                    "Keine Swissbit Hardware-TSE als Windows-Laufwerk gefunden.");
            }

            using var session = OpenSession(mount);
            using var info = OpenInfo(session.Context);

            var state = InfoU32(
                info.Pointer,
                "worm_info_initializationState");

            if (state == InitDecommissioned)
            {
                return new TseActivationResult(
                    false,
                    "Diese TSE ist außer Betrieb gesetzt und kann nicht erneut aktiviert werden.");
            }

            if (state == InitUninitialized)
            {
                var changedPuk = OptionalInfoBool(
                    info.Pointer,
                    "worm_info_hasChangedPuk");

                var changedAdminPin = OptionalInfoBool(
                    info.Pointer,
                    "worm_info_hasChangedAdminPin");

                var changedTimeAdminPin = OptionalInfoBool(
                    info.Pointer,
                    "worm_info_hasChangedTimeAdminPin");

                // This is deliberately conservative. A wrong credential seed can
                // permanently block a production TSE after repeated attempts.
                if (changedPuk || changedAdminPin || changedTimeAdminPin)
                {
                    return new TseActivationResult(
                        false,
                        "TSE ist noch nicht initialisiert, aber PIN/PUK wurden bereits verändert. " +
                        "Automatisches Setup wird aus Sicherheitsgründen verweigert. " +
                        "Bitte mit den vorhandenen Admin-Zugangsdaten manuell fortsetzen.");
                }

                // The first self-test may intentionally report CLIENT_NOT_REGISTERED.
                var selfTest = RunSelfTest(
                    session.Context,
                    request.ClientId);

                if (selfTest != WormOk &&
                    selfTest != WormErrorClientNotRegistered)
                {
                    return Fail(
                        "Erster Selbsttest fehlgeschlagen",
                        selfTest);
                }

                var setup = Setup(
                    session.Context,
                    request);

                if (setup != WormOk)
                {
                    return Fail(
                        "Swissbit TSE-Setup fehlgeschlagen. Nicht erneut blind versuchen",
                        setup);
                }
            }
            else
            {
                var ensureClient = EnsureClientRegistered(
                    session.Context,
                    request.ClientId,
                    request.AdminPin);

                if (ensureClient != WormOk)
                    return Fail("Client-Registrierung fehlgeschlagen", ensureClient);
            }

            var finalSelfTest = RunSelfTest(
                session.Context,
                request.ClientId);

            if (finalSelfTest == WormErrorClientNotRegistered)
            {
                var ensureClient = EnsureClientRegistered(
                    session.Context,
                    request.ClientId,
                    request.AdminPin);

                if (ensureClient != WormOk)
                    return Fail("Client-Registrierung fehlgeschlagen", ensureClient);

                finalSelfTest = RunSelfTest(
                    session.Context,
                    request.ClientId);
            }

            if (finalSelfTest != WormOk)
                return Fail("Selbsttest nach Aktivierung fehlgeschlagen", finalSelfTest);

            var ctssResult = EnsureCtssActive(
                session.Context,
                request.AdminPin,
                info.Pointer);

            if (ctssResult != WormOk)
                return Fail("CTSS-Aktivierung fehlgeschlagen", ctssResult);

            var timeResult = UpdateTime(
                session.Context,
                request.TimeAdminPin);

            if (timeResult != WormOk)
                return Fail("TSE-Zeit konnte nicht aktualisiert werden", timeResult);

            RefreshInfo(info.Pointer);

            var device = ReadDeviceInfoFromInfo(
                info.Pointer,
                mount);

            var initialized =
                InfoU32(info.Pointer, "worm_info_initializationState") ==
                InitInitialized;

            var selfTestPassed =
                OptionalInfoBool(info.Pointer, "worm_info_hasPassedSelfTest");

            var validTime =
                OptionalInfoBool(info.Pointer, "worm_info_hasValidTime");

            var ctss =
                OptionalInfoBoolAny(
                    info.Pointer,
                    "worm_info_isCtssInterfaceActive",
                    "worm_info_isErsInterfaceActive");

            if (!initialized || !selfTestPassed || !validTime || !ctss)
            {
                return new TseActivationResult(
                    false,
                    "TSE wurde angesprochen, ist aber noch nicht vollständig betriebsbereit.",
                    device);
            }

            return new TseActivationResult(
                true,
                $"Swissbit TSE ist betriebsbereit · {device.SerialNumber}",
                device);
        }, ct);
    }

    public async Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!TransactionAvailable)
            return MissingTransactionApi();

        return await RunExclusiveAsync(() =>
        {
            var mount = RequireMountPoint();
            using var session = OpenSession(mount);

            var ready = PrepareForTransaction(
                session.Context,
                request.ClientId,
                request.TimeAdminPin);

            if (ready.Code != WormOk)
                return TxFail("TSE ist für die Transaktion nicht bereit", ready);

            using var response = CreateTransactionResponse(session.Context);

            var result = WithAnsi(request.ClientId, client =>
                WithBytes(request.ProcessData, (data, dataLength) =>
                    WithAnsi(request.ProcessType, processType =>
                        Get<WormTransactionStart>("worm_transaction_start")(
                            session.Context,
                            client,
                            data,
                            dataLength,
                            processType,
                            response.Pointer))));

            if (result != WormOk)
                return TxFail("TSE StartTransaction fehlgeschlagen", result);

            return ReadTransactionResult(
                response.Pointer,
                "TSE-Transaktion gestartet.");
        }, ct);
    }

    public async Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!TransactionAvailable)
            return MissingTransactionApi();

        return await RunExclusiveAsync(() =>
        {
            var mount = RequireMountPoint();
            using var session = OpenSession(mount);

            var ready = PrepareForTransaction(
                session.Context,
                request.ClientId,
                request.TimeAdminPin);

            if (ready.Code != WormOk)
                return TxFail("TSE ist für UpdateTransaction nicht bereit", ready);

            using var response = CreateTransactionResponse(session.Context);

            var result = WithAnsi(request.ClientId, client =>
                WithBytes(request.ProcessData, (data, dataLength) =>
                    WithAnsi(request.ProcessType, processType =>
                        Get<WormTransactionUpdate>("worm_transaction_update")(
                            session.Context,
                            client,
                            request.TransactionNumber,
                            data,
                            dataLength,
                            processType,
                            response.Pointer))));

            if (result != WormOk)
                return TxFail("TSE UpdateTransaction fehlgeschlagen", result);

            return ReadTransactionResult(
                response.Pointer,
                "TSE-Transaktion aktualisiert.");
        }, ct);
    }

    public async Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!TransactionAvailable)
            return MissingTransactionApi();

        return await RunExclusiveAsync(() =>
        {
            var mount = RequireMountPoint();
            using var session = OpenSession(mount);

            var ready = PrepareForTransaction(
                session.Context,
                request.ClientId,
                request.TimeAdminPin);

            if (ready.Code != WormOk)
                return TxFail("TSE ist für FinishTransaction nicht bereit", ready);

            using var response = CreateTransactionResponse(session.Context);

            var result = WithAnsi(request.ClientId, client =>
                WithBytes(request.ProcessData, (data, dataLength) =>
                    WithAnsi(request.ProcessType, processType =>
                        Get<WormTransactionFinish>("worm_transaction_finish")(
                            session.Context,
                            client,
                            request.TransactionNumber,
                            data,
                            dataLength,
                            processType,
                            response.Pointer))));

            if (result != WormOk)
                return TxFail("TSE FinishTransaction fehlgeschlagen", result);

            return ReadTransactionResult(
                response.Pointer,
                "TSE-Transaktion abgeschlossen.");
        }, ct);
    }

    public async Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default)
    {
        EnsureLoaded();

        if (!ExportAvailable)
        {
            return new TseExportResult(
                false,
                "Swissbit TAR-Export-API ist nicht verfügbar.");
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new TseExportResult(
                false,
                "Zieldatei für TAR-Export fehlt.");
        }

        return await RunExclusiveAsync(() =>
        {
            var mount = RequireMountPoint();
            using var session = OpenSession(mount);

            Directory.CreateDirectory(
                Path.GetDirectoryName(targetPath) ??
                Environment.CurrentDirectory);

            using var file = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            WormExportTarCallback callback =
                (chunk, chunkLength, _) =>
                {
                    if (chunk == IntPtr.Zero || chunkLength == 0)
                        return 0;

                    var buffer = new byte[checked((int)chunkLength)];
                    Marshal.Copy(chunk, buffer, 0, buffer.Length);
                    file.Write(buffer, 0, buffer.Length);
                    return 0;
                };

            var result =
                Get<WormExportTar>("worm_export_tar")(
                    session.Context,
                    callback,
                    IntPtr.Zero);

            GC.KeepAlive(callback);

            if (result != WormOk)
            {
                try { file.Close(); File.Delete(targetPath); } catch { }

                return new TseExportResult(
                    false,
                    $"TSE TAR-Export fehlgeschlagen: {ErrorText(result)}");
            }

            file.Flush(true);

            return new TseExportResult(
                true,
                "TSE TAR-Export erfolgreich.",
                targetPath);
        }, ct);
    }

    private TseDeviceInfo ReadDeviceInfo(
        NativeSession session,
        string mountPoint)
    {
        using var info = OpenInfo(session.Context);
        return ReadDeviceInfoFromInfo(info.Pointer, mountPoint);
    }

    private TseDeviceInfo ReadDeviceInfoFromInfo(
        IntPtr info,
        string mountPoint)
    {
        RefreshInfo(info);

        var serialBytes = InfoByteArray64(
            info,
            "worm_info_tseSerialNumber");

        var serial = serialBytes.Length == 0
            ? ""
            : Convert.ToHexString(serialBytes);

        var formFactor = OptionalInfoChars(
            info,
            "worm_info_formFactor");

        var description = OptionalInfoChars(
            info,
            "worm_info_tseDescription");

        var hardwareVersion = OptionalVersion(
            info,
            "worm_info_hardwareVersion");

        var softwareVersion = OptionalVersion(
            info,
            "worm_info_softwareVersion");

        var expiryRaw = OptionalInfoU64(
            info,
            "worm_info_certificateExpirationDate");

        DateOnly? expiry = null;
        DateTimeOffset? expiryInstant = null;

        if (expiryRaw > 0)
        {
            try
            {
                expiryInstant =
                    DateTimeOffset.FromUnixTimeSeconds(
                        checked((long)expiryRaw));

                // Compatibility/display only. Safety uses expiryInstant.
                expiry = DateOnly.FromDateTime(
                    expiryInstant.Value.UtcDateTime);
            }
            catch
            {
            }
        }

        var init = InfoU32(
            info,
            "worm_info_initializationState");

        var initText = init switch
        {
            InitUninitialized => "UNINITIALIZED",
            InitInitialized => "INITIALIZED",
            InitDecommissioned => "DECOMMISSIONED",
            _ => $"UNKNOWN({init})"
        };

        return new TseDeviceInfo(
            "Swissbit",
            string.IsNullOrWhiteSpace(description)
                ? "Hardware-TSE"
                : description,
            // The WORM metadata available here does not provide a validated
            // generation discriminator. Do not repurpose the description or
            // hardware/software revision as generation evidence. A documented
            // adapter mapping can populate this field after real-device capture.
            "",
            string.IsNullOrWhiteSpace(formFactor)
                ? "USB/Storage"
                : formFactor,
            serial,
            "",
            expiry,
            mountPoint,
            _sdkVersion,
            hardwareVersion,
            softwareVersion,
            initText,
            OptionalInfoBool(info, "worm_info_hasPassedSelfTest"),
            OptionalInfoBool(info, "worm_info_hasValidTime"),
            OptionalInfoBoolAny(
                info,
                "worm_info_isCtssInterfaceActive",
                "worm_info_isErsInterfaceActive"),
            CertificateExpiresAtUtc: expiryInstant);
    }

    private readonly record struct PrepareTransactionResult(
        int Code,
        bool TimeAdminPinRejected = false,
        int? RemainingRetries = null);

    private PrepareTransactionResult PrepareForTransaction(
        IntPtr context,
        string clientId,
        string timeAdminPin)
    {
        using var info = OpenInfo(context);

        if (InfoU32(info.Pointer, "worm_info_initializationState") !=
            InitInitialized)
        {
            return new(0x10FF);
        }

        if (!OptionalInfoBool(
            info.Pointer,
            "worm_info_hasPassedSelfTest"))
        {
            var selfTest = RunSelfTest(context, clientId);

            if (selfTest != WormOk)
                return new(selfTest);

            RefreshInfo(info.Pointer);
        }

        if (!OptionalInfoBool(
            info.Pointer,
            "worm_info_hasValidTime"))
        {
            if (string.IsNullOrWhiteSpace(timeAdminPin))
                return new(0x1002);

            var time = UpdateTime(context, timeAdminPin);
            if (time.Code != WormOk)
                return time;
        }

        if (!OptionalInfoBoolAny(
            info.Pointer,
            "worm_info_isCtssInterfaceActive",
            "worm_info_isErsInterfaceActive"))
        {
            return new(0x1053);
        }

        return new(WormOk);
    }

    private int EnsureClientRegistered(
        IntPtr context,
        string clientId,
        string adminPin)
    {
        var selfTest = RunSelfTest(context, clientId);

        if (selfTest == WormOk)
            return WormOk;

        if (selfTest != WormErrorClientNotRegistered)
            return selfTest;

        var login = UserLogin(context, UserAdmin, adminPin);
        if (login.Code != WormOk)
            return login.Code;

        try
        {
            var register = WithAnsi(
                clientId,
                client =>
                    Get<WormTseRegisterClient>(
                        "worm_tse_registerClient")(
                        context,
                        client));

            return register;
        }
        finally
        {
            UserLogout(context, UserAdmin);
        }
    }

    private int EnsureCtssActive(
        IntPtr context,
        string adminPin,
        IntPtr info)
    {
        RefreshInfo(info);

        if (OptionalInfoBoolAny(
            info,
            "worm_info_isCtssInterfaceActive",
            "worm_info_isErsInterfaceActive"))
        {
            return WormOk;
        }

        var login = UserLogin(context, UserAdmin, adminPin);
        if (login.Code != WormOk)
            return login.Code;

        try
        {
            if (Has("worm_tse_ctss_enable"))
            {
                return Get<WormSimpleContext>(
                    "worm_tse_ctss_enable")(context);
            }

            if (Has("worm_tse_ers_enable"))
            {
                return Get<WormSimpleContext>(
                    "worm_tse_ers_enable")(context);
            }

            return 0xF000;
        }
        finally
        {
            UserLogout(context, UserAdmin);
        }
    }

    private PrepareTransactionResult UpdateTime(
        IntPtr context,
        string timeAdminPin)
    {
        var login = UserLogin(
            context,
            UserTimeAdmin,
            timeAdminPin);

        if (login.Code != WormOk)
        {
            return new(
                login.Code,
                TimeAdminPinRejected: true,
                RemainingRetries: login.RemainingRetries);
        }

        try
        {
            var unixTime = checked(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            return new(
                Get<WormTseUpdateTime>(
                    "worm_tse_updateTime")(
                    context,
                    unixTime));
        }
        finally
        {
            UserLogout(context, UserTimeAdmin);
        }
    }

    private readonly record struct UserLoginResult(
        int Code,
        int RemainingRetries);

    private UserLoginResult UserLogin(
        IntPtr context,
        int user,
        string pin)
    {
        return WithAnsi(pin, pinPointer =>
        {
            var retries = 0;

            var code =
                Get<WormUserLogin>(
                    "worm_user_login")(
                    context,
                    user,
                    pinPointer,
                    pin.Length,
                    out retries);

            return new UserLoginResult(
                code,
                retries);
        });
    }

    private int UserLogout(
        IntPtr context,
        int user)
    {
        if (!Has("worm_user_logout"))
            return WormOk;

        return Get<WormUserLogout>(
            "worm_user_logout")(
            context,
            user);
    }

    private int RunSelfTest(
        IntPtr context,
        string clientId)
    {
        return WithAnsi(
            clientId,
            client =>
                Get<WormTseRunSelfTest>(
                    "worm_tse_runSelfTest")(
                    context,
                    client));
    }

    private int Setup(
        IntPtr context,
        TseActivationRequest request)
    {
        return WithAnsi(request.CredentialSeed, seed =>
            WithAnsi(request.Puk, puk =>
                WithAnsi(request.AdminPin, admin =>
                    WithAnsi(request.TimeAdminPin, timeAdmin =>
                        WithAnsi(request.ClientId, client =>
                            Get<WormTseSetup>(
                                "worm_tse_setup")(
                                context,
                                seed,
                                request.CredentialSeed.Length,
                                puk,
                                request.Puk.Length,
                                admin,
                                request.AdminPin.Length,
                                timeAdmin,
                                request.TimeAdminPin.Length,
                                client))))));
    }

    private static string? ValidateActivationRequest(
        TseActivationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return "Client-ID / Kassen-ID fehlt.";

        if (request.ClientId.Contains('/') ||
            request.ClientId.Contains('\\'))
        {
            return "Client-ID darf keinen Slash oder Backslash enthalten.";
        }

        if (request.ClientId.Length > 30)
            return "Client-ID darf maximal 30 Zeichen lang sein.";

        if (request.AdminPin.Length != 5)
            return "Admin-PIN muss für Swissbit genau 5 Zeichen haben.";

        if (request.TimeAdminPin.Length != 5)
            return "TimeAdmin-PIN muss für Swissbit genau 5 Zeichen haben.";

        if (request.Puk.Length != 6)
            return "Admin-PUK muss für Swissbit genau 6 Zeichen haben.";

        if (string.IsNullOrWhiteSpace(request.CredentialSeed))
            return "Credential-Seed fehlt. Bitte ausschließlich den Seed des TSE-Lieferanten verwenden.";

        if (request.CredentialSeed.Any(ch => ch > 127) ||
            request.AdminPin.Any(ch => ch > 255) ||
            request.TimeAdminPin.Any(ch => ch > 255) ||
            request.Puk.Any(ch => ch > 255) ||
            request.ClientId.Any(ch => ch > 127))
        {
            return "Swissbit-Zugangsdaten und Client-ID müssen 1-Byte/ASCII-kompatibel sein.";
        }

        return null;
    }

    private TseTransactionResult ReadTransactionResult(
        IntPtr response,
        string message)
    {
        var transactionNumber =
            Get<WormResponseU64>(
                "worm_transaction_response_transactionNumber")(
                response);

        var signatureCounter =
            Get<WormResponseU64>(
                "worm_transaction_response_signatureCounter")(
                response);

        var logTimeRaw =
            Get<WormResponseU64>(
                "worm_transaction_response_logTime")(
                response);

        var serial = Convert.ToHexString(
            ResponseBytes(
                response,
                "worm_transaction_response_serialNumber"));

        var signature = Convert.ToBase64String(
            ResponseBytes(
                response,
                "worm_transaction_response_signature"));

        DateTimeOffset? logTime = null;

        if (logTimeRaw > 0)
        {
            try
            {
                logTime = DateTimeOffset.FromUnixTimeSeconds(
                    checked((long)logTimeRaw));
            }
            catch
            {
            }
        }

        return new TseTransactionResult(
            true,
            message,
            transactionNumber,
            signatureCounter,
            logTime,
            serial,
            signature);
    }

    private byte[] ResponseBytes(
        IntPtr response,
        string exportName)
    {
        var pointer = IntPtr.Zero;
        ulong length = 0;

        Get<WormResponseBytes>(
            exportName)(
            response,
            out pointer,
            out length);

        if (pointer == IntPtr.Zero || length == 0)
            return Array.Empty<byte>();

        if (length > int.MaxValue)
            throw new InvalidOperationException(
                "Swissbit response buffer ist unerwartet groß.");

        var buffer = new byte[(int)length];
        Marshal.Copy(pointer, buffer, 0, buffer.Length);
        return buffer;
    }

    private TseActivationResult Fail(
        string prefix,
        int error) =>
        new(
            false,
            $"{prefix}: {ErrorText(error)}");

    private TseTransactionResult TxFail(
        string prefix,
        int error) =>
        new(
            false,
            $"{prefix}: {ErrorText(error)}");

    private TseTransactionResult TxFail(
        string prefix,
        PrepareTransactionResult result) =>
        new(
            false,
            result.TimeAdminPinRejected
                ? $"{prefix}: TimeAdmin-PIN-Anmeldung fehlgeschlagen · verbleibende Versuche: {result.RemainingRetries?.ToString() ?? "unbekannt"} · {ErrorText(result.Code)}"
                : $"{prefix}: {ErrorText(result.Code)}",
            TimeAdminPinRejected:
                result.TimeAdminPinRejected,
            TimeAdminRemainingRetries:
                result.RemainingRetries);

    private static TseTransactionResult MissingTransactionApi() =>
        new(
            false,
            "Swissbit Transaktions-API ist nicht vollständig verfügbar.");

    private NativeSession OpenSession(string mountPoint)
    {
        var init = Get<WormInit>("worm_init");

        var result = init(out var context, mountPoint);

        if (result != WormOk || context == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"worm_init({mountPoint}) fehlgeschlagen: {ErrorText(result)}");
        }

        return new NativeSession(
            context,
            Get<WormCleanup>("worm_cleanup"));
    }

    private NativeInfo OpenInfo(IntPtr context)
    {
        var pointer =
            Get<WormInfoNew>("worm_info_new")(context);

        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "worm_info_new lieferte keinen Info-Kontext.");
        }

        var info = new NativeInfo(
            pointer,
            Get<WormInfoFree>("worm_info_free"));

        RefreshInfo(pointer);
        return info;
    }

    private void RefreshInfo(IntPtr info)
    {
        var result =
            Get<WormInfoRead>("worm_info_read")(info);

        if (result != WormOk &&
            result != WormErrorNoCard)
        {
            throw new InvalidOperationException(
                $"worm_info_read fehlgeschlagen: {ErrorText(result)}");
        }
    }

    private uint InfoU32(
        IntPtr info,
        string name) =>
        Get<WormInfoU32>(name)(info);

    private ulong OptionalInfoU64(
        IntPtr info,
        string name) =>
        Has(name)
            ? Get<WormInfoU64>(name)(info)
            : 0UL;

    private bool OptionalInfoBool(
        IntPtr info,
        string name) =>
        Has(name) &&
        Get<WormInfoU32>(name)(info) != 0;

    private bool OptionalInfoBoolAny(
        IntPtr info,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (Has(name) &&
                Get<WormInfoU32>(name)(info) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private string OptionalInfoChars(
        IntPtr info,
        string name)
    {
        if (!Has(name))
            return "";

        var pointer =
            Get<WormInfoChars>(name)(info);

        return pointer == IntPtr.Zero
            ? ""
            : Marshal.PtrToStringAnsi(pointer) ?? "";
    }

    private string OptionalVersion(
        IntPtr info,
        string name)
    {
        if (!Has(name))
            return "";

        var packed =
            Get<WormInfoU32>(name)(info);

        var major = (packed & 0xFFFF0000) >> 16;
        var minor = (packed & 0x0000FF00) >> 8;
        var patch = packed & 0x000000FF;

        return $"{major}.{minor}.{patch}";
    }

    private byte[] InfoByteArray64(
        IntPtr info,
        string name)
    {
        var pointer = IntPtr.Zero;
        ulong length = 0;

        Get<WormInfoBytes64>(name)(
            info,
            out pointer,
            out length);

        if (pointer == IntPtr.Zero || length == 0)
            return Array.Empty<byte>();

        if (length > int.MaxValue)
            throw new InvalidOperationException(
                "Swissbit info buffer ist unerwartet groß.");

        var buffer = new byte[(int)length];
        Marshal.Copy(pointer, buffer, 0, buffer.Length);
        return buffer;
    }

    private NativeTransactionResponse CreateTransactionResponse(
        IntPtr context)
    {
        var pointer =
            Get<WormTransactionResponseNew>(
                "worm_transaction_response_new")(
                context);

        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Swissbit TransactionResponse konnte nicht erstellt werden.");
        }

        return new NativeTransactionResponse(
            pointer,
            Get<WormTransactionResponseFree>(
                "worm_transaction_response_free"));
    }

    private string RequireMountPoint()
    {
        var mount =
            FindCandidateMountPoints().FirstOrDefault();

        if (string.IsNullOrWhiteSpace(mount))
        {
            throw new InvalidOperationException(
                "Keine Swissbit Hardware-TSE gefunden.");
        }

        return mount;
    }

    // The scan itself lives in SwissbitDeviceScan, because recognising the
    // stick must also work when this bridge could not load the DLL at all.
    private static IReadOnlyList<string> FindCandidateMountPoints() =>
        SwissbitDeviceScan.FindMountPoints();

    private async Task<T> RunExclusiveAsync<T>(
        Func<T> action,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await Task.Run(action, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_library != IntPtr.Zero ||
            !string.IsNullOrWhiteSpace(_loadError))
        {
            return;
        }

        lock (_loadSync)
        {
            if (_library != IntPtr.Zero ||
                !string.IsNullOrWhiteSpace(_loadError))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                _loadError =
                    "Swissbit Hardware-TSE ist in TOR nur für Windows vorgesehen.";
                return;
            }

            foreach (var candidate in CandidateLibraries())
            {
                try
                {
                    if (!Path.IsPathRooted(candidate) &&
                        !string.Equals(
                            candidate,
                            "WormAPI.dll",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Path.IsPathRooted(candidate) &&
                        !File.Exists(candidate))
                    {
                        continue;
                    }

                    if (NativeLibrary.TryLoad(candidate, out _library))
                    {
                        _libraryPath = candidate;
                        LoadExports();

                        if (Has("worm_getVersion"))
                        {
                            var versionPointer =
                                Get<WormGetVersion>(
                                    "worm_getVersion")();

                            _sdkVersion =
                                versionPointer == IntPtr.Zero
                                    ? ""
                                    : Marshal.PtrToStringAnsi(versionPointer) ?? "";
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                    _loadError =
                        $"Swissbit SDK konnte nicht geladen werden: {ex.Message}";
                }
            }

            if (_library == IntPtr.Zero &&
                string.IsNullOrWhiteSpace(_loadError))
            {
                _loadError =
                    "WormAPI.dll nicht gefunden. Offizielles Swissbit SDK in " +
                    "'SwissbitSdk' installieren bzw. beim Build einbinden.";
            }
        }
    }

    private IEnumerable<string> CandidateLibraries()
    {
        var result = new List<string>();
        var baseDir = AppContext.BaseDirectory;

        var environmentPath =
            Environment.GetEnvironmentVariable(
                "TOR_SWISSBIT_WORMAPI");

        if (!string.IsNullOrWhiteSpace(environmentPath))
            result.Add(environmentPath);

        try
        {
            if (File.Exists(ConfiguredLibraryPathFile))
            {
                var configured =
                    File.ReadAllText(
                        ConfiguredLibraryPathFile).Trim();

                if (!string.IsNullOrWhiteSpace(configured))
                    result.Add(configured);
            }
        }
        catch
        {
            // A damaged/unreadable config file must not block startup.
        }

        result.Add(
            Path.Combine(
                baseDir,
                "SwissbitSdk",
                "WormAPI.dll"));

        result.Add(
            Path.Combine(
                baseDir,
                "WormAPI.dll"));

        result.Add(
            Path.Combine(
                baseDir,
                "plugins",
                "swissbit",
                "WormAPI.dll"));

        result.Add("WormAPI.dll");

        return result
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SearchRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Downloads")
        };

        return roots
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesBounded(
        string root,
        string fileName,
        int maxDepth,
        int maxResults,
        CancellationToken ct)
    {
        var result = new List<string>();
        var queue =
            new Queue<(string Path, int Depth)>();

        queue.Enqueue((root, 0));

        while (queue.Count > 0 &&
               result.Count < maxResults)
        {
            ct.ThrowIfCancellationRequested();

            var current =
                queue.Dequeue();

            string? direct = null;

            try
            {
                direct =
                    Path.Combine(
                        current.Path,
                        fileName);
            }
            catch
            {
                direct = null;
            }

            if (!string.IsNullOrWhiteSpace(direct))
            {
                try
                {
                    if (File.Exists(direct))
                    {
                        result.Add(direct);

                        if (result.Count >= maxResults)
                            break;
                    }
                }
                catch
                {
                    // Access/path error: skip this item.
                }
            }

            if (current.Depth >= maxDepth)
                continue;

            string[] directories;

            try
            {
                directories =
                    Directory
                        .EnumerateDirectories(
                            current.Path)
                        .ToArray();
            }
            catch
            {
                directories =
                    Array.Empty<string>();
            }

            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();

                string name;

                try
                {
                    name =
                        Path.GetFileName(
                            directory);
                }
                catch
                {
                    continue;
                }

                if (ShouldSkipDirectory(name))
                    continue;

                queue.Enqueue(
                    (directory,
                     current.Depth + 1));
            }
        }

        return result;
    }

    private static bool ShouldSkipDirectory(
        string directoryName) =>
        directoryName.Equals(
            "$Recycle.Bin",
            StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(
            "System Volume Information",
            StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(
            "WindowsApps",
            StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(
            "WinSxS",
            StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(
            "node_modules",
            StringComparison.OrdinalIgnoreCase) ||
        directoryName.Equals(
            ".git",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatibleLibrary(
        string path)
    {
        if (!OperatingSystem.IsWindows() ||
            !File.Exists(path))
        {
            return false;
        }

        IntPtr library = IntPtr.Zero;

        try
        {
            if (!NativeLibrary.TryLoad(
                path,
                out library))
            {
                return false;
            }

            foreach (var exportName in new[]
            {
                "worm_init",
                "worm_cleanup",
                "worm_getVersion",
                "worm_info_new",
                "worm_info_read",
                "worm_info_free",
                "worm_info_tseSerialNumber",
                "worm_info_formFactor",
                "worm_info_certificateExpirationDate",
                "worm_info_initializationState"
            })
            {
                if (!NativeLibrary.TryGetExport(
                    library,
                    exportName,
                    out _))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (library != IntPtr.Zero)
            {
                try
                {
                    NativeLibrary.Free(library);
                }
                catch
                {
                }
            }
        }
    }

    private void ResetLoadedLibrary()
    {
        _exports.Clear();
        _sdkVersion = "";
        _libraryPath = "";
        _loadError = "";

        if (_library != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_library);
            }
            catch
            {
            }

            _library = IntPtr.Zero;
        }
    }

    private void LoadExports()
    {
        _exports.Clear();

        foreach (var name in KnownExports)
        {
            if (NativeLibrary.TryGetExport(
                _library,
                name,
                out var pointer))
            {
                _exports[name] = pointer;
            }
        }
    }

    private bool Has(string name) =>
        _exports.ContainsKey(name);

    private T Get<T>(string name)
        where T : Delegate
    {
        if (!_exports.TryGetValue(
            name,
            out var pointer))
        {
            throw new MissingMethodException(
                $"Swissbit WORM API export fehlt: {name}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(
            pointer);
    }

    private static T WithAnsi<T>(
        string value,
        Func<IntPtr, T> action)
    {
        var pointer =
            Marshal.StringToCoTaskMemAnsi(value ?? "");

        try
        {
            return action(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static int WithBytes(
        byte[]? data,
        Func<IntPtr, ulong, int> action)
    {
        if (data is null || data.Length == 0)
            return action(IntPtr.Zero, 0);

        var pointer =
            Marshal.AllocHGlobal(data.Length);

        try
        {
            Marshal.Copy(
                data,
                0,
                pointer,
                data.Length);

            return action(
                pointer,
                checked((ulong)data.Length));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string ErrorText(int error) =>
        error switch
        {
            0 => "NO_ERROR (0x0000)",
            1 => "INVALID_PARAMETER (0x0001)",
            2 => "NO_WORM_CARD (0x0002)",
            3 => "IO_ERROR (0x0003)",
            4 => "TIMEOUT (0x0004)",
            0x1002 => "NO_TIME_SET (0x1002)",
            0x1008 => "TRANSACTION_NOT_STARTED (0x1008)",
            0x100A => "CERTIFICATE_EXPIRED (0x100A)",
            0x100F => "NOT_AUTHORIZED (0x100F)",
            0x1010 => "MAX_REGISTERED_CLIENTS_REACHED (0x1010)",
            0x1011 => "CLIENT_NOT_REGISTERED (0x1011)",
            0x1013 => "CLIENT_HAS_UNFINISHED_TRANSACTIONS (0x1013)",
            0x1014 => "TSE_HAS_UNFINISHED_TRANSACTIONS (0x1014)",
            0x1050 => "NEEDS_PUK_CHANGE (0x1050)",
            0x1051 => "NEEDS_PIN_CHANGE (0x1051)",
            0x1053 => "NEEDS_ACTIVE_CTSS (0x1053)",
            0x1054 => "NEEDS_SELF_TEST (0x1054)",
            0x1055 => "NEEDS_SELF_TEST_PASSED (0x1055)",
            0x10FD => "TSE_ALREADY_INITIALIZED (0x10FD)",
            0x10FE => "TSE_DECOMMISSIONED (0x10FE)",
            0x10FF => "TSE_NOT_INITIALIZED (0x10FF)",
            0x1100 => "AUTHENTICATION_FAILED (0x1100)",
            0x1201 => "AUTHENTICATION_PIN_BLOCKED (0x1201)",
            0x1202 => "USER_NOT_LOGGED_IN (0x1202)",
            0xF000 => "COMMAND_NOT_FOUND (0xF000)",
            0xFF00 => "SIGNATURE_ERROR (0xFF00)",
            _ => $"WORM_ERROR 0x{error:X4}"
        };

    public void Dispose()
    {
        _gate.Dispose();

        if (_library != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_library);
            }
            catch
            {
            }

            _library = IntPtr.Zero;
        }
    }

    private sealed class NativeSession : IDisposable
    {
        private readonly WormCleanup _cleanup;

        public NativeSession(
            IntPtr context,
            WormCleanup cleanup)
        {
            Context = context;
            _cleanup = cleanup;
        }

        public IntPtr Context { get; }

        public void Dispose()
        {
            if (Context != IntPtr.Zero)
            {
                try { _cleanup(Context); } catch { }
            }
        }
    }

    private sealed class NativeInfo : IDisposable
    {
        private readonly WormInfoFree _free;

        public NativeInfo(
            IntPtr pointer,
            WormInfoFree free)
        {
            Pointer = pointer;
            _free = free;
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                try { _free(Pointer); } catch { }
            }
        }
    }

    private sealed class NativeTransactionResponse : IDisposable
    {
        private readonly WormTransactionResponseFree _free;

        public NativeTransactionResponse(
            IntPtr pointer,
            WormTransactionResponseFree free)
        {
            Pointer = pointer;
            _free = free;
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                try { _free(Pointer); } catch { }
            }
        }
    }

    private static readonly string[] KnownExports =
    {
        "worm_init",
        "worm_cleanup",
        "worm_getVersion",
        "worm_info_new",
        "worm_info_read",
        "worm_info_free",
        "worm_info_tseSerialNumber",
        "worm_info_formFactor",
        "worm_info_tseDescription",
        "worm_info_hardwareVersion",
        "worm_info_softwareVersion",
        "worm_info_certificateExpirationDate",
        "worm_info_initializationState",
        "worm_info_hasPassedSelfTest",
        "worm_info_hasValidTime",
        "worm_info_isCtssInterfaceActive",
        "worm_info_isErsInterfaceActive",
        "worm_info_hasChangedPuk",
        "worm_info_hasChangedAdminPin",
        "worm_info_hasChangedTimeAdminPin",
        "worm_tse_runSelfTest",
        "worm_tse_setup",
        "worm_tse_registerClient",
        "worm_tse_ctss_enable",
        "worm_tse_ers_enable",
        "worm_tse_updateTime",
        "worm_user_login",
        "worm_user_logout",
        "worm_transaction_response_new",
        "worm_transaction_response_free",
        "worm_transaction_response_transactionNumber",
        "worm_transaction_response_signatureCounter",
        "worm_transaction_response_logTime",
        "worm_transaction_response_serialNumber",
        "worm_transaction_response_signature",
        "worm_transaction_start",
        "worm_transaction_update",
        "worm_transaction_finish",
        "worm_export_tar"
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormInit(
        out IntPtr context,
        [MarshalAs(UnmanagedType.LPStr)] string mountPoint);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormCleanup(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WormGetVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WormInfoNew(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormInfoRead(IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WormInfoFree(IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint WormInfoU32(IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong WormInfoU64(IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WormInfoChars(IntPtr info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WormInfoBytes64(
        IntPtr info,
        out IntPtr data,
        out ulong length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTseRunSelfTest(
        IntPtr context,
        IntPtr clientId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTseSetup(
        IntPtr context,
        IntPtr credentialSeed,
        int credentialSeedLength,
        IntPtr adminPuk,
        int adminPukLength,
        IntPtr adminPin,
        int adminPinLength,
        IntPtr timeAdminPin,
        int timeAdminPinLength,
        IntPtr clientId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTseRegisterClient(
        IntPtr context,
        IntPtr clientId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormSimpleContext(
        IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTseUpdateTime(
        IntPtr context,
        ulong unixTime);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormUserLogin(
        IntPtr context,
        int userId,
        IntPtr pin,
        int pinLength,
        out int remainingRetries);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormUserLogout(
        IntPtr context,
        int userId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WormTransactionResponseNew(
        IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WormTransactionResponseFree(
        IntPtr response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong WormResponseU64(
        IntPtr response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WormResponseBytes(
        IntPtr response,
        out IntPtr data,
        out ulong length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTransactionStart(
        IntPtr context,
        IntPtr clientId,
        IntPtr processData,
        ulong processDataLength,
        IntPtr processType,
        IntPtr response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTransactionUpdate(
        IntPtr context,
        IntPtr clientId,
        ulong transactionNumber,
        IntPtr processData,
        ulong processDataLength,
        IntPtr processType,
        IntPtr response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormTransactionFinish(
        IntPtr context,
        IntPtr clientId,
        ulong transactionNumber,
        IntPtr processData,
        ulong processDataLength,
        IntPtr processType,
        IntPtr response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormExportTar(
        IntPtr context,
        WormExportTarCallback callback,
        IntPtr callbackData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WormExportTarCallback(
        IntPtr chunk,
        uint chunkLength,
        IntPtr callbackData);
}
