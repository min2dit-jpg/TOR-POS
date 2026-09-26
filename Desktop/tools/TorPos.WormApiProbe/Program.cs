using System.Runtime.InteropServices;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Dieses Prüfwerkzeug ist für die Windows-WORMAPI.DLL vorgesehen.");
    return 2;
}

var recoveryMode =
    args.Length == 4 &&
    string.Equals(args[1], "--recovery", StringComparison.OrdinalIgnoreCase);

if (args.Length != 1 && !recoveryMode)
{
    PrintUsage();
    return 2;
}

var path = Path.GetFullPath(args[0]);

if (!File.Exists(path))
{
    Console.Error.WriteLine($"Datei nicht gefunden: {path}");
    return 2;
}

if (!string.Equals(Path.GetFileName(path), "WormAPI.dll", StringComparison.OrdinalIgnoreCase))
    Console.WriteLine("WARNUNG: Erwarteter Dateiname ist WormAPI.dll.");

IntPtr library = IntPtr.Zero;

try
{
    if (!NativeLibrary.TryLoad(path, out library))
    {
        Console.Error.WriteLine("NativeLibrary.TryLoad fehlgeschlagen.");
        return 3;
    }

    var required = new[]
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

    var missing = required
        .Where(name => !NativeLibrary.TryGetExport(library, name, out _))
        .ToArray();

    // Useful for a stronger hardware acceptance/recovery workflow. These are
    // not production requirements today, so their absence does not fail the
    // default probe.
    var recommended = new[]
    {
        "worm_signatureAlgorithm",
        "worm_logTimeFormat",
        "worm_info_tsePublicKey",
        "worm_transaction_listStartedTransactions",
        "worm_transaction_lastResponse",
        "worm_export_tar_ignore_io_errors",
        "worm_getLogMessageCertificate"
    };

    var optionalMissing = recommended
        .Where(name => !NativeLibrary.TryGetExport(library, name, out _))
        .ToArray();

    var version = "";
    if (NativeLibrary.TryGetExport(library, "worm_getVersion", out var versionExport))
    {
        var getVersion = Marshal.GetDelegateForFunctionPointer<WormGetVersion>(versionExport);
        var ptr = getVersion();
        if (ptr != IntPtr.Zero)
            version = Marshal.PtrToStringAnsi(ptr) ?? "";
    }

    Console.WriteLine("TOR POS · Swissbit WORM API Probe");
    Console.WriteLine($"Datei: {path}");
    Console.WriteLine($"Version: {(version.Length == 0 ? "(nicht lesbar)" : version)}");
    Console.WriteLine($"Benötigte Exporte: {required.Length}");
    Console.WriteLine($"Vorhanden: {required.Length - missing.Length}");
    Console.WriteLine($"Fehlend: {missing.Length}");

    foreach (var name in missing)
        Console.WriteLine($"  FEHLT: {name}");

    Console.WriteLine($"Empfohlene Zusatz-Exporte: {recommended.Length - optionalMissing.Length}/{recommended.Length}");
    foreach (var name in optionalMissing)
        Console.WriteLine($"  OPTIONAL FEHLT: {name}");

    if (missing.Length != 0)
    {
        Console.WriteLine("ERGEBNIS: API-Exportprüfung NICHT BESTANDEN.");
        return 4;
    }

    Console.WriteLine("ERGEBNIS: API-Exportprüfung BESTANDEN.");
    Console.WriteLine("Hinweis: Das ist noch kein Hardware-, TSE- oder Fiskal-Abnahmetest.");

    if (!recoveryMode)
        return 0;

    var recoveryExports = new[]
    {
        "worm_transaction_listStartedTransactions",
        "worm_transaction_lastResponse",
        "worm_transaction_response_new",
        "worm_transaction_response_free",
        "worm_transaction_response_transactionNumber",
        "worm_transaction_response_signatureCounter",
        "worm_transaction_response_logTime",
        "worm_transaction_response_serialNumber",
        "worm_transaction_response_signature"
    };

    var recoveryMissing = recoveryExports
        .Where(name => !NativeLibrary.TryGetExport(library, name, out _))
        .ToArray();

    if (recoveryMissing.Length > 0)
    {
        Console.Error.WriteLine("Recovery-Diagnose nicht verfügbar. Fehlende Exporte:");
        foreach (var name in recoveryMissing)
            Console.Error.WriteLine($"  FEHLT: {name}");
        return 6;
    }

    var mountPoint = args[2];
    var clientId = args[3];

    if (string.IsNullOrWhiteSpace(mountPoint) || string.IsNullOrWhiteSpace(clientId))
    {
        Console.Error.WriteLine("Mountpoint und Client-ID dürfen nicht leer sein.");
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine("READ-ONLY RECOVERY-DIAGNOSE");
    Console.WriteLine("Es werden keine Setup-, Login-, Zeit-, Start-, Update- oder Finish-Aufrufe ausgeführt.");

    var init = Get<WormInit>(library, "worm_init");
    var cleanup = Get<WormCleanup>(library, "worm_cleanup");

    var initResult = init(out var context, mountPoint);
    if (initResult != 0 || context == IntPtr.Zero)
    {
        Console.Error.WriteLine($"worm_init fehlgeschlagen: 0x{initResult:X4}");
        return 7;
    }

    try
    {
        var started = ReadStartedTransactions(library, context, clientId);
        Console.WriteLine($"Offene Transaktionen laut TSE: {started.Count}");
        foreach (var transaction in started)
            Console.WriteLine($"  TXN {transaction}");

        var last = ReadLastResponse(library, context, clientId);
        if (!last.Available)
        {
            Console.WriteLine($"Letzte Response: nicht verfügbar (0x{last.Error:X4})");
        }
        else
        {
            Console.WriteLine(
                $"Letzte Response: TXN {last.TransactionNumber}, " +
                $"Signaturzähler {last.SignatureCounter}, " +
                $"LogTime {FormatUnix(last.LogTime)}, " +
                $"Serienbytes {last.SerialLength}, Signaturbytes {last.SignatureLength}");
        }

        Console.WriteLine("Recovery-Diagnose abgeschlossen.");
        return 0;
    }
    finally
    {
        try { cleanup(context); }
        catch { }
    }
}
catch (BadImageFormatException ex)
{
    Console.Error.WriteLine(
        "DLL-Architektur ist mit diesem Prozess nicht kompatibel (z. B. x86/x64): " +
        ex.Message);
    return 5;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 9;
}
finally
{
    if (library != IntPtr.Zero)
    {
        try { NativeLibrary.Free(library); }
        catch { }
    }
}

static void PrintUsage()
{
    Console.WriteLine("Verwendung:");
    Console.WriteLine("  dotnet run --project tools/TorPos.WormApiProbe -- <Pfad zu WormAPI.dll>");
    Console.WriteLine();
    Console.WriteLine("Optional, nur lesende Recovery-Diagnose mit angeschlossener Test-TSE:");
    Console.WriteLine("  dotnet run --project tools/TorPos.WormApiProbe -- <WormAPI.dll> --recovery <Mountpoint> <Client-ID>");
}

static T Get<T>(IntPtr library, string name)
    where T : Delegate
{
    if (!NativeLibrary.TryGetExport(library, name, out var pointer))
        throw new MissingMethodException($"WORM API export fehlt: {name}");

    return Marshal.GetDelegateForFunctionPointer<T>(pointer);
}

static IReadOnlyList<ulong> ReadStartedTransactions(
    IntPtr library,
    IntPtr context,
    string clientId)
{
    const int capacity = 62;
    var buffer = Marshal.AllocHGlobal(capacity * sizeof(long));

    try
    {
        for (var i = 0; i < capacity; i++)
            Marshal.WriteInt64(buffer, i * sizeof(long), 0L);

        var list = Get<WormTransactionListStartedTransactions>(
            library,
            "worm_transaction_listStartedTransactions");

        return WithAnsi(clientId, client =>
        {
            var result = list(
                context,
                client,
                0,
                buffer,
                capacity,
                out var count);

            if (result != 0)
                throw new InvalidOperationException(
                    $"worm_transaction_listStartedTransactions fehlgeschlagen: 0x{result:X4}");

            if (count < 0 || count > capacity)
                throw new InvalidOperationException(
                    $"Swissbit lieferte eine ungültige Anzahl offener Transaktionen: {count}");

            var transactions = new List<ulong>(count);
            for (var i = 0; i < count; i++)
                transactions.Add(unchecked((ulong)Marshal.ReadInt64(buffer, i * sizeof(long))));

            return (IReadOnlyList<ulong>)transactions;
        });
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static LastResponseInfo ReadLastResponse(
    IntPtr library,
    IntPtr context,
    string clientId)
{
    var create = Get<WormTransactionResponseNew>(
        library,
        "worm_transaction_response_new");
    var free = Get<WormTransactionResponseFree>(
        library,
        "worm_transaction_response_free");
    var lastResponse = Get<WormTransactionLastResponse>(
        library,
        "worm_transaction_lastResponse");

    var response = create(context);
    if (response == IntPtr.Zero)
        throw new InvalidOperationException("TransactionResponse konnte nicht erstellt werden.");

    try
    {
        var error = WithAnsi(clientId, client =>
            lastResponse(context, client, response));

        if (error != 0)
            return new LastResponseInfo(false, error, 0, 0, 0, 0, 0);

        var transactionNumber = Get<WormResponseU64>(
            library,
            "worm_transaction_response_transactionNumber")(response);
        var signatureCounter = Get<WormResponseU64>(
            library,
            "worm_transaction_response_signatureCounter")(response);
        var logTime = Get<WormResponseU64>(
            library,
            "worm_transaction_response_logTime")(response);

        var serialLength = ResponseLength(
            Get<WormResponseBytes>(
                library,
                "worm_transaction_response_serialNumber"),
            response);
        var signatureLength = ResponseLength(
            Get<WormResponseBytes>(
                library,
                "worm_transaction_response_signature"),
            response);

        return new LastResponseInfo(
            true,
            0,
            transactionNumber,
            signatureCounter,
            logTime,
            serialLength,
            signatureLength);
    }
    finally
    {
        try { free(response); }
        catch { }
    }
}

static ulong ResponseLength(
    WormResponseBytes read,
    IntPtr response)
{
    read(response, out var pointer, out var length);
    return pointer == IntPtr.Zero ? 0 : length;
}

static T WithAnsi<T>(
    string value,
    Func<IntPtr, T> action)
{
    var pointer = Marshal.StringToCoTaskMemAnsi(value ?? "");
    try
    {
        return action(pointer);
    }
    finally
    {
        Marshal.FreeCoTaskMem(pointer);
    }
}

static string FormatUnix(ulong raw)
{
    if (raw == 0)
        return "0";

    try
    {
        return DateTimeOffset
            .FromUnixTimeSeconds(checked((long)raw))
            .ToString("O");
    }
    catch
    {
        return raw.ToString();
    }
}

readonly record struct LastResponseInfo(
    bool Available,
    int Error,
    ulong TransactionNumber,
    ulong SignatureCounter,
    ulong LogTime,
    ulong SerialLength,
    ulong SignatureLength);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate IntPtr WormGetVersion();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int WormInit(
    out IntPtr context,
    [MarshalAs(UnmanagedType.LPStr)] string mountPoint);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int WormCleanup(IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int WormTransactionListStartedTransactions(
    IntPtr context,
    IntPtr clientId,
    uint skip,
    IntPtr transactionNumbers,
    int capacity,
    out int count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate IntPtr WormTransactionResponseNew(IntPtr context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void WormTransactionResponseFree(IntPtr response);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int WormTransactionLastResponse(
    IntPtr context,
    IntPtr clientId,
    IntPtr response);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate ulong WormResponseU64(IntPtr response);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void WormResponseBytes(
    IntPtr response,
    out IntPtr data,
    out ulong length);
