using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R172 hard watchdog around the Swissbit native WORM API.
///
/// Native P/Invoke cannot be force-cancelled safely from the calling thread.
/// Every device/transaction/export call therefore runs in a short-lived TOR POS
/// helper process. If the vendor DLL blocks, only that helper is terminated and
/// the cashier process receives a TimeoutException, which the existing
/// TseFailSafeService converts into a documented TSE-Ausfall.
/// </summary>
public sealed class SwissbitWatchdogBridge : ISwissbitSdkBridge, IDisposable
{
    public const string WorkerSwitch = "--tor-swissbit-worker";

    private readonly SwissbitWormApiBridge _local = new();
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _transactionTimeout;
    private readonly TimeSpan _activationTimeout;
    private readonly TimeSpan _exportTimeout;

    public SwissbitWatchdogBridge(
        TimeSpan? probeTimeout = null,
        TimeSpan? transactionTimeout = null,
        TimeSpan? activationTimeout = null,
        TimeSpan? exportTimeout = null)
    {
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(10);
        _transactionTimeout = transactionTimeout ?? TimeSpan.FromSeconds(10);
        _activationTimeout = activationTimeout ?? TimeSpan.FromSeconds(90);
        _exportTimeout = exportTimeout ?? TimeSpan.FromMinutes(3);
    }

    // Library discovery/configuration is local and does not access a TSE
    // transaction. The potentially blocking hardware/native operations below
    // are isolated in the helper process.
    public bool IsAvailable => _local.IsAvailable;
    public bool ActivationAvailable => _local.ActivationAvailable;
    public bool TransactionAvailable => _local.TransactionAvailable;
    public bool ExportAvailable => _local.ExportAvailable;

    public TseRuntimeStatus GetRuntimeStatus() => _local.GetRuntimeStatus();

    public Task<IReadOnlyList<string>> FindInstalledLibrariesAsync(
        CancellationToken ct = default)
        => _local.FindInstalledLibrariesAsync(ct);

    public TseRuntimeStatus ConfigureLibrary(string libraryPath)
        => _local.ConfigureLibrary(libraryPath);

    public Task<IReadOnlyList<TseDeviceInfo>> FindDevicesAsync(
        CancellationToken ct = default)
        => RunAsync<object, IReadOnlyList<TseDeviceInfo>>(
            "find_devices",
            new { },
            _probeTimeout,
            ct);

    public Task<TseActivationResult> ActivateAsync(
        TseActivationRequest request,
        CancellationToken ct = default)
        => RunAsync<TseActivationRequest, TseActivationResult>(
            "activate",
            request,
            _activationTimeout,
            ct);

    public Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
        => RunAsync<TseTransactionStartRequest, TseTransactionResult>(
            "start_transaction",
            request,
            _transactionTimeout,
            ct);

    public Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default)
        => RunAsync<TseTransactionUpdateRequest, TseTransactionResult>(
            "update_transaction",
            request,
            _transactionTimeout,
            ct);

    public Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default)
        => RunAsync<TseTransactionFinishRequest, TseTransactionResult>(
            "finish_transaction",
            request,
            _transactionTimeout,
            ct);

    public Task<TseExportResult> ExportTarAsync(
        string targetPath,
        CancellationToken ct = default)
        => RunAsync<SwissbitExportRequest, TseExportResult>(
            "export_tar",
            new SwissbitExportRequest(targetPath),
            _exportTimeout,
            ct);

    private static ProcessStartInfo WorkerStartInfo()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException(
                "Swissbit-Watchdog kann den TOR-POS-Prozesspfad nicht bestimmen.");

        var info = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Framework-dependent developer/setup runs can have dotnet.exe as the
        // process executable. In that case re-enter the TorPos.App entry DLL.
        var entry = Assembly.GetEntryAssembly()?.Location ?? "";
        var hostName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entry) ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(entry),
                    "TorPos.App",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Swissbit-Watchdog ist nur aus dem TOR-POS-Anwendungsprozess verfügbar.");
            }
            info.ArgumentList.Add(entry);
        }

        info.ArgumentList.Add(WorkerSwitch);
        return info;
    }

    private static async Task<TResponse> RunAsync<TRequest, TResponse>(
        string operation,
        TRequest payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = WorkerStartInfo() };
        if (!process.Start())
            throw new InvalidOperationException(
                "Swissbit-Watchdog-Prozess konnte nicht gestartet werden.");

        var request = new SwissbitWorkerRequest(
            operation,
            JsonSerializer.Serialize(payload));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.StandardInput.WriteAsync(
            JsonSerializer.Serialize(request).AsMemory(),
            ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        var waitTask = process.WaitForExitAsync(ct);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);
        var completed = await Task.WhenAny(waitTask, timeoutTask);

        if (completed != waitTask)
        {
            TryKill(process);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Swissbit TSE antwortet seit {timeout.TotalSeconds:0} Sekunden nicht. " +
                "Der isolierte TSE-Prozess wurde beendet; TOR POS bleibt bedienbar.");
        }

        try
        {
            await waitTask;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        SwissbitWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SwissbitWorkerResponse>(stdout);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Swissbit-Watchdog lieferte keine gültige Antwort." +
                (string.IsNullOrWhiteSpace(stderr) ? "" : " Worker: " + Safe(stderr)),
                ex);
        }

        if (response is null)
            throw new InvalidOperationException("Swissbit-Watchdog lieferte eine leere Antwort.");

        if (!response.Success)
            throw new InvalidOperationException(
                "Swissbit TSE-Aufruf fehlgeschlagen: " + Safe(response.Error));

        var result = JsonSerializer.Deserialize<TResponse>(response.Payload);
        return result
            ?? throw new InvalidOperationException(
                "Swissbit-Watchdog-Antwort konnte nicht gelesen werden.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Timeout is already authoritative; kill errors must not hide it.
        }
    }

    private static string Safe(string? text)
    {
        var value = (text ?? "")
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return value.Length <= 700 ? value : value[..700] + "…";
    }

    public void Dispose() => _local.Dispose();

    internal sealed record SwissbitWorkerRequest(string Operation, string Payload);
    internal sealed record SwissbitWorkerResponse(bool Success, string Payload, string Error);
    internal sealed record SwissbitExportRequest(string TargetPath);
}

/// <summary>
/// Entry point used only by Program before Avalonia/mutex startup.
/// Request data, including activation credentials, travels through stdin and
/// never appears in command-line arguments.
/// </summary>
public static class SwissbitWorkerHost
{
    public static bool IsWorkerCommand(string[] args)
        => args.Length == 1 &&
           string.Equals(
               args[0],
               SwissbitWatchdogBridge.WorkerSwitch,
               StringComparison.Ordinal);

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        SwissbitWatchdogBridge.SwissbitWorkerResponse response;
        try
        {
            var input = await Console.In.ReadToEndAsync(ct);
            var request =
                JsonSerializer.Deserialize<SwissbitWatchdogBridge.SwissbitWorkerRequest>(input)
                ?? throw new InvalidDataException("Worker-Anfrage fehlt.");

            using var bridge = new SwissbitWormApiBridge();
            object result = request.Operation switch
            {
                "find_devices" =>
                    await bridge.FindDevicesAsync(ct),

                "activate" =>
                    await bridge.ActivateAsync(
                        Required<TseActivationRequest>(request.Payload),
                        ct),

                "start_transaction" =>
                    await bridge.StartTransactionAsync(
                        Required<TseTransactionStartRequest>(request.Payload),
                        ct),

                "update_transaction" =>
                    await bridge.UpdateTransactionAsync(
                        Required<TseTransactionUpdateRequest>(request.Payload),
                        ct),

                "finish_transaction" =>
                    await bridge.FinishTransactionAsync(
                        Required<TseTransactionFinishRequest>(request.Payload),
                        ct),

                "export_tar" =>
                    await bridge.ExportTarAsync(
                        Required<SwissbitWatchdogBridge.SwissbitExportRequest>(
                            request.Payload).TargetPath,
                        ct),

                _ => throw new InvalidOperationException(
                    "Unbekannte Swissbit-Worker-Operation.")
            };

            response = new(
                true,
                JsonSerializer.Serialize(result, result.GetType()),
                "");
        }
        catch (Exception ex)
        {
            // Never echo request payload: it can contain Admin PIN/PUK.
            response = new(
                false,
                "",
                ex.GetType().Name + ": " + ex.Message);
        }

        await Console.Out.WriteAsync(
            JsonSerializer.Serialize(response).AsMemory(),
            ct);
        await Console.Out.FlushAsync();

        return response.Success ? 0 : 2;
    }

    private static T Required<T>(string json)
        => JsonSerializer.Deserialize<T>(json)
           ?? throw new InvalidDataException(
               "Swissbit-Worker-Nutzdaten fehlen oder sind ungültig.");
}
