using TorPos.Infrastructure;

// G-3: the update helper starts the installer with admin rights minutes after
// it was downloaded and checked. It now locks the file, checks SHA-256 (and a
// pinned signer) again right before the start, and runs PowerShell from the
// Windows system folder instead of whatever "powershell.exe" PATH finds.
public static class G3UpdateHelperTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var path = TorUpdateService.PowerShellPath;
        assert(
            Path.IsPathRooted(path) &&
            path.EndsWith(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"), StringComparison.OrdinalIgnoreCase),
            $"G-3 the update helper and signature check run PowerShell from the system folder, never via PATH ({path})");

        var source = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/TorUpdateService.cs"));
        var lockAt = source.IndexOf("[System.IO.File]::Open($f,'Open','Read','Read')", StringComparison.Ordinal);
        var hashAt = source.IndexOf("if($hash -ne $env:TOR_UPDATE_SHA256)", StringComparison.Ordinal);
        var signAt = source.IndexOf("Get-AuthenticodeSignature -LiteralPath $f", StringComparison.Ordinal);
        var startAt = source.IndexOf("Start-Process -FilePath $f", StringComparison.Ordinal);
        assert(
            !source.Contains("new ProcessStartInfo(\"powershell.exe\")", StringComparison.Ordinal) &&
            lockAt > 0 && hashAt > lockAt && signAt > hashAt && startAt > signAt &&
            source.Contains("psi.Environment[\"TOR_UPDATE_SHA256\"] = staged.Manifest.Sha256", StringComparison.Ordinal) &&
            source.Contains("psi.Environment[\"TOR_UPDATE_THUMB\"] = TorRelease.UpdateSignerThumbprint", StringComparison.Ordinal),
            "G-3 the installer is locked against replacement and its SHA-256 and pinned signature are checked again immediately before it is started with admin rights");
        return Task.CompletedTask;
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
}
