using System.Runtime.InteropServices;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Dieses Prüfwerkzeug ist für die Windows-WORMAPI.DLL vorgesehen.");
    return 2;
}

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.WriteLine("Verwendung:");
    Console.WriteLine("  dotnet run --project tools/TorPos.WormApiProbe -- <Pfad zu WormAPI.dll>");
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
    // probe. They let TOR cross-check QR/master data directly from the SDK and
    // inspect unfinished/last transactions after a crash.
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

    string version = "";
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

    if (missing.Length == 0)
    {
        Console.WriteLine("ERGEBNIS: API-Exportprüfung BESTANDEN.");
        Console.WriteLine("Hinweis: Das ist noch kein Hardware-, TSE- oder Fiskal-Abnahmetest.");
        return 0;
    }

    Console.WriteLine("ERGEBNIS: API-Exportprüfung NICHT BESTANDEN.");
    return 4;
}
catch (BadImageFormatException ex)
{
    Console.Error.WriteLine("DLL-Architektur ist mit diesem Prozess nicht kompatibel (z. B. x86/x64): " + ex.Message);
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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate IntPtr WormGetVersion();
