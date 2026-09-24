using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record TorUpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("published_at")] string PublishedAt,
    [property: JsonPropertyName("mandatory")] bool Mandatory,
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("signer_thumbprint")] string SignerThumbprint,
    [property: JsonPropertyName("release_notes")] string ReleaseNotes);

public sealed record TorUpdateCheckResult(bool UpdateAvailable, string Message, TorUpdateManifest? Manifest = null);
public sealed record TorStagedUpdate(string InstallerPath, TorUpdateManifest Manifest, string BackupPath);

/// <summary>
/// TOR-owned updater boundary. It never changes fiscal/customer data. Remote update metadata
/// must come from HTTPS (localhost HTTP is development-only), the setup is hash verified, and
/// remote production installs additionally require a valid Authenticode signature/thumbprint.
/// </summary>
public sealed class TorUpdateService
{
    private readonly ISettingsRepository _settings;
    private readonly DatabaseBackupService _backup;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TorUpdateService(ISettingsRepository settings, DatabaseBackupService backup)
    {
        _settings = settings;
        _backup = backup;
    }

    /// <summary>
    /// Removes only disposable update-cache files. Fiscal data, reports, product images,
    /// backups and recovery evidence are deliberately excluded. The currently staged
    /// installer and the two newest completed installers are retained.
    /// </summary>
    public static async Task CleanupCacheAsync(ISettingsRepository settings, CancellationToken ct = default, string? updatesPath = null)
    {
        try
        {
            var cachePath = string.IsNullOrWhiteSpace(updatesPath) ? AppPaths.UpdatesPath : updatesPath;
            Directory.CreateDirectory(cachePath);
            var now = DateTime.UtcNow;

            foreach (var file in new DirectoryInfo(cachePath).GetFiles("*.download"))
            {
                ct.ThrowIfCancellationRequested();
                if (file.LastWriteTimeUtc < now.AddHours(-24))
                {
                    try { file.Delete(); } catch { }
                }
            }

            var stagedRaw = await settings.GetAsync("update.staged_path", "", ct);
            string staged = "";
            if (!string.IsNullOrWhiteSpace(stagedRaw))
            {
                try { staged = Path.GetFullPath(stagedRaw); } catch { staged = ""; }
            }

            var installers = new DirectoryInfo(cachePath)
                .GetFiles("TOR-POS-Pro-Setup-*.exe")
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .ToList();

            var keep = installers.Take(2)
                .Select(x => x.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (staged.Length > 0) keep.Add(staged);

            foreach (var file in installers)
            {
                ct.ThrowIfCancellationRequested();
                if (!keep.Contains(file.FullName))
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Cache cleanup is best-effort and must never block cashier startup.
        }
    }

    public async Task<TorUpdateCheckResult> CheckAsync(string edition, bool force = false, CancellationToken ct = default)
    {
        if (!force && !await EnabledAsync(ct))
            return new(false, "Automatische Update-Prüfung ist deaktiviert.");

        var baseUrl = await ResolveBaseUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new(false, "Kein TOR-Update-Server konfiguriert.");

        var root = ValidateServerUrl(baseUrl);
        var endpoint = new Uri(root,
            $"api/v1/updates/check?version={Uri.EscapeDataString(TorRelease.Version)}&revision={Uri.EscapeDataString(TorRelease.Revision)}&edition={Uri.EscapeDataString(edition)}");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TOR-POS-Pro", TorRelease.UserAgentVersion));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["update.last_check_utc"] = DateTimeOffset.UtcNow.ToString("O")
        }, ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Update-Server antwortet mit {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        var rootJson = doc.RootElement;
        if (!rootJson.TryGetProperty("update_available", out var available) || !available.GetBoolean())
        {
            await _settings.SaveManyAsync(new Dictionary<string, string>
            {
                ["update.last_status"] = "aktuell"
            }, ct);
            return new(false, "TOR POS ist aktuell.");
        }

        if (!rootJson.TryGetProperty("manifest", out var manifestJson))
            throw new InvalidDataException("Update-Manifest fehlt.");
        var manifest = manifestJson.Deserialize<TorUpdateManifest>(JsonOptions)
            ?? throw new InvalidDataException("Update-Manifest kann nicht gelesen werden.");
        ValidateManifest(root, manifest);
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["update.last_status"] = $"verfügbar:{manifest.Version}",
            ["update.last_seen_version"] = manifest.Version
        }, ct);
        return new(true, $"Neue Version verfügbar: {manifest.Revision}", manifest);
    }

    public async Task<TorStagedUpdate> DownloadAndStageAsync(TorUpdateManifest manifest, CancellationToken ct = default)
    {
        var baseUrl = await ResolveBaseUrlAsync(ct);
        var root = ValidateServerUrl(baseUrl);
        ValidateManifest(root, manifest);
        var download = new Uri(manifest.DownloadUrl, UriKind.Absolute);
        if (!string.Equals(download.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(download.Host, root.Host, StringComparison.OrdinalIgnoreCase) ||
            download.Port != root.Port)
            throw new InvalidOperationException("Update-Download muss vom konfigurierten TOR-Update-Server stammen.");

        Directory.CreateDirectory(AppPaths.UpdatesPath);
        var safeVersion = string.Concat(manifest.Version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
        var target = Path.Combine(AppPaths.UpdatesPath, $"TOR-POS-Pro-Setup-{safeVersion}.exe");
        var temporary = target + ".download";

        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) })
        using (var request = new HttpRequestMessage(HttpMethod.Get, download))
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TOR-POS-Pro", TorRelease.UserAgentVersion));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Update-Download fehlgeschlagen: {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > 500_000_000)
                throw new InvalidDataException("Update-Datei ist unerwartet groß.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            var buffer = new byte[1024 * 128];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;
                total += read;
                if (total > 500_000_000)
                {
                    await output.DisposeAsync();
                    File.Delete(temporary);
                    throw new InvalidDataException("Update-Datei ist unerwartet groß.");
                }
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
        }

        var actual = await Sha256Async(temporary, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(manifest.Sha256)))
        {
            File.Delete(temporary);
            throw new CryptographicException("Update-Prüfsumme stimmt nicht. Datei wurde verworfen.");
        }

        if (RequiresSignatureEnforcement(root))
        {
            var pinnedSigner = TorRelease.UpdateSignerThumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(pinnedSigner))
            {
                File.Delete(temporary);
                throw new CryptographicException("Remote-Update ist gesperrt: TOR Code-Signing-Zertifikat ist in diesem Build noch nicht fest hinterlegt.");
            }
            var manifestSigner = (manifest.SignerThumbprint ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pinnedSigner), Encoding.ASCII.GetBytes(manifestSigner)))
            {
                File.Delete(temporary);
                throw new CryptographicException("Update-Signatur entspricht nicht dem in TOR POS fest hinterlegten Herausgeber-Zertifikat.");
            }
            await VerifyAuthenticodeAsync(temporary, pinnedSigner, ct);
        }

        File.Move(temporary, target, true);
        var backupDirectory = await _settings.GetAsync("backup.directory", "", ct);
        var backupPath = await _backup.CreateBackupAsync(backupDirectory, ct);
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["update.staged_version"] = manifest.Version,
            ["update.staged_path"] = target,
            ["update.backup_path"] = backupPath
        }, ct);
        return new(target, manifest, backupPath);
    }

    // G-3: never resolve "powershell.exe" through PATH / the working directory.
    internal static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    public string ScheduleInstallAfterExit(TorStagedUpdate staged)
    {
        if (!File.Exists(staged.InstallerPath))
            throw new FileNotFoundException("Vorbereitetes Update wurde nicht gefunden.", staged.InstallerPath);

        // Detached Windows helper: waits until TOR POS has fully closed (including the normal
        // exit backup / printer shutdown) and only then starts the Inno Setup installer.
        // Environment variables avoid command-line quoting problems with customer paths.
        // G-3: the installer was verified when it was downloaded, but the helper
        // starts it minutes later with admin rights. It therefore opens the file
        // read-only with FileShare.Read (nobody can replace or change it until
        // Setup has started), checks the SHA-256 again and, when a signer is
        // pinned, the Authenticode signature, and only then starts it.
        var command = "$ErrorActionPreference='Stop';" +
                      "function Stop-Update($m){try{Add-Content -LiteralPath $env:TOR_UPDATE_LOG -Value ((Get-Date).ToString('o')+' '+$m)}catch{};exit 5};" +
                      "$target=[int]$env:TOR_UPDATE_PID;" +
                      "while(Get-Process -Id $target -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 500};" +
                      "$f=$env:TOR_UPDATE_INSTALLER;" +
                      "try{$lock=[System.IO.File]::Open($f,'Open','Read','Read')}catch{Stop-Update 'Installer nicht lesbar'};" +
                      "try{" +
                      "$sha=[System.Security.Cryptography.SHA256]::Create();" +
                      "$hash=[System.BitConverter]::ToString($sha.ComputeHash($lock)).Replace('-','');" +
                      "if($hash -ne $env:TOR_UPDATE_SHA256){Stop-Update 'SHA-256 stimmt nicht'};" +
                      "if($env:TOR_UPDATE_THUMB){$sig=Get-AuthenticodeSignature -LiteralPath $f;" +
                      "if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $env:TOR_UPDATE_THUMB){Stop-Update 'Signatur ungueltig'}};" +
                      "Start-Process -FilePath $f -ArgumentList '/SILENT','/NORESTART' -Verb RunAs;" +
                      "}finally{$lock.Dispose()}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        psi.Environment["TOR_UPDATE_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["TOR_UPDATE_INSTALLER"] = staged.InstallerPath;
        psi.Environment["TOR_UPDATE_SHA256"] = staged.Manifest.Sha256.ToUpperInvariant();
        psi.Environment["TOR_UPDATE_THUMB"] = TorRelease.UpdateSignerThumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        psi.Environment["TOR_UPDATE_LOG"] = Path.Combine(AppPaths.UpdatesPath, "update-helper.log");
        _ = Process.Start(psi) ?? throw new InvalidOperationException("Update-Helfer konnte nicht gestartet werden.");
        return "PowerShell update helper";
    }

    public async Task<bool> ShouldBackgroundCheckAsync(CancellationToken ct = default)
    {
        if (!await EnabledAsync(ct)) return false;
        var raw = await _settings.GetAsync("update.last_check_utc", "", ct);
        return !DateTimeOffset.TryParse(raw, out var last) || DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(6);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        _settings.SaveManyAsync(new Dictionary<string, string> { ["update.enabled"] = enabled ? "true" : "false" }, ct);

    public async Task<bool> EnabledAsync(CancellationToken ct = default) =>
        !string.Equals(await _settings.GetAsync("update.enabled", "true", ct), "false", StringComparison.OrdinalIgnoreCase);

    public Task SetServerUrlAsync(string baseUrl, CancellationToken ct = default) =>
        _settings.SaveManyAsync(new Dictionary<string, string> { ["update.server_url"] = (baseUrl ?? "").Trim() }, ct);

    public async Task<string> ResolveBaseUrlAsync(CancellationToken ct = default)
    {
        var explicitUrl = (await _settings.GetAsync("update.server_url", "", ct)).Trim();
        if (explicitUrl.Length > 0) return explicitUrl;
        var cloudRaw = await _settings.GetAsync("cloud.configuration", "", ct);
        if (string.IsNullOrWhiteSpace(cloudRaw)) return "";
        try { return JsonSerializer.Deserialize<TorCloudConfiguration>(cloudRaw)?.BaseUrl?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static Uri ValidateServerUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var root) ||
            root.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("TOR Update Server-URL ist ungültig.");
        if (root.Scheme == "http" && !IsLoopback(root))
            throw new InvalidOperationException("Produktive TOR Updates benötigen HTTPS. HTTP ist nur für localhost erlaubt.");
        if (root.UserInfo.Length > 0)
            throw new InvalidOperationException("Update-Server-URL darf keine Zugangsdaten enthalten.");
        return root;
    }

    private static bool IsLoopback(Uri uri) =>
        UpdateTrustPolicy.IsLoopback(uri);

    // R114: true only in a developer build. A customer Release build never
    // waives signature verification, not even for a loopback update server.
    private static bool DevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    private static bool RequiresSignatureEnforcement(Uri root) =>
        UpdateTrustPolicy.RequiresSignatureEnforcement(root, DevelopmentBuild);

    private static void ValidateManifest(Uri root, TorUpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Revision))
            throw new InvalidDataException("Update-Version/Revision fehlt.");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var download))
            throw new InvalidDataException("Update-Download-URL ist ungültig.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Update-SHA256 fehlt oder ist ungültig.");
        if (RequiresSignatureEnforcement(root))
        {
            var pinnedSigner = TorRelease.UpdateSignerThumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            var manifestSigner = (manifest.SignerThumbprint ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(pinnedSigner))
                throw new InvalidDataException("Remote-Update ist gesperrt, bis das TOR Code-Signing-Zertifikat im Release fest hinterlegt ist.");
            if (!string.Equals(pinnedSigner, manifestSigner, StringComparison.Ordinal))
                throw new InvalidDataException("Update-Manifest nennt nicht das fest hinterlegte TOR Herausgeber-Zertifikat.");
        }
        _ = download;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static async Task VerifyAuthenticodeAsync(string path, string expectedThumbprint, CancellationToken ct)
    {
        var command = "$s=Get-AuthenticodeSignature -LiteralPath $env:TOR_UPDATE_FILE;" +
                      "if($s.Status -ne 'Valid'){Write-Output ('STATUS='+$s.Status);exit 3};" +
                      "$t=($s.SignerCertificate.Thumbprint -replace ' ','').ToUpperInvariant();" +
                      "$e=($env:TOR_UPDATE_THUMB -replace ' ','').ToUpperInvariant();" +
                      "if($t -ne $e){Write-Output ('THUMB='+$t);exit 4};Write-Output ('THUMB='+$t);";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var psi = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        psi.Environment["TOR_UPDATE_FILE"] = path;
        psi.Environment["TOR_UPDATE_THUMB"] = expectedThumbprint;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows Signaturprüfung konnte nicht gestartet werden.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        if (process.ExitCode != 0)
            throw new CryptographicException("Authenticode-Prüfung fehlgeschlagen. " + output.Trim());
    }
}
