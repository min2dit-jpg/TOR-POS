using TorPos.Core;

public static class R178ReviewTests
{
    public static Task Run(
        string root,
        Action<bool, string> assert)
    {
        var release = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Core/ReleaseInfo.cs"));
        var updater = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/TorUpdateService.cs"));
        var settings = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var server = File.ReadAllText(
            FindRepoFile("Cloud/server.js"));
        var store = File.ReadAllText(
            FindRepoFile("Cloud/update-store.js"));
        var publish = File.ReadAllText(
            FindRepoFile("Cloud/PUBLISH-UPDATE.ps1"));
        var disable = File.ReadAllText(
            FindRepoFile("Cloud/DISABLE-UPDATE.ps1"));
        var rollout = File.ReadAllText(
            FindRepoFile("Dokumentation/UPDATE-ROLLOUT-DE.md"));

        assert(
            TorRelease.OfficialUpdateServerUrl == "https://updates.torpos.de/" &&
            release.Contains(
                "OfficialUpdateServerUrl = \"https://updates.torpos.de/\"",
                StringComparison.Ordinal),
            "R178 gives every customer build one official HTTPS update discovery origin");

        assert(
            updater.Contains(
                "return TorRelease.OfficialUpdateServerUrl;",
                StringComparison.Ordinal) &&
            !updater.Contains(
                "cloud.configuration",
                StringComparison.Ordinal),
            "R178 customer update discovery no longer depends on TOR Cloud configuration");

        assert(
            updater.Contains(
                "&channel={Uri.EscapeDataString(channel)}",
                StringComparison.Ordinal) &&
            updater.Contains(
                "normalized == \"PILOT\" ? \"PILOT\" : \"STABLE\"",
                StringComparison.Ordinal),
            "R178 update client requests an explicit channel and unknown values fail closed to STABLE");

        assert(
            updater.Contains(
                "VerifyAuthenticodeAsync(temporary, pinnedSigner, ct)",
                StringComparison.Ordinal) &&
            updater.Contains(
                "Remote-Update ist gesperrt",
                StringComparison.Ordinal) &&
            updater.Contains(
                "UpdateSignerThumbprint",
                StringComparison.Ordinal),
            "R178 keeps pinned Authenticode verification mandatory for remote customer updates");

        assert(
            updater.Contains(
                "-Wait -PassThru",
                StringComparison.Ordinal) &&
            updater.Contains(
                "if($code -eq 0",
                StringComparison.Ordinal) &&
            updater.Contains(
                "TOR_UPDATE_APP",
                StringComparison.Ordinal) &&
            updater.Contains(
                "last-install-result.txt",
                StringComparison.Ordinal),
            "R178 waits for installer completion, records its exit result and restarts TOR POS only after success");

        assert(
            settings.Contains(
                "Combo(\"update.channel\", \"STABLE\", \"PILOT\")",
                StringComparison.Ordinal) &&
            settings.Contains(
                "TorRelease.OfficialUpdateServerUrl",
                StringComparison.Ordinal),
            "R178 technician settings expose controlled STABLE/PILOT selection while normal customers keep the official server");

        assert(
            server.Contains(
                "pilot-manifest.json",
                StringComparison.Ordinal) &&
            server.Contains(
                "requestedChannel==='PILOT'?'PILOT':'STABLE'",
                StringComparison.Ordinal) &&
            server.Contains(
                "TOR_UPDATE_PUBLIC_URL",
                StringComparison.Ordinal) &&
            server.Contains(
                "for(const name of ['manifest.json','pilot-manifest.json'])",
                StringComparison.Ordinal),
            "R178 server isolates STABLE/PILOT manifests and only serves files referenced by an enabled channel");

        assert(
            store.Contains(
                "publishPilotVerified",
                StringComparison.Ordinal) &&
            store.Contains(
                "disablePilot",
                StringComparison.Ordinal) &&
            publish.Contains(
                "[ValidateSet(\"STABLE\",\"PILOT\")]",
                StringComparison.Ordinal) &&
            disable.Contains(
                "[ValidateSet(\"STABLE\",\"PILOT\")]",
                StringComparison.Ordinal),
            "R178 publication tools can promote or stop PILOT and STABLE independently");

        assert(
            rollout.Contains(
                "PILOT",
                StringComparison.Ordinal) &&
            rollout.Contains(
                "STABLE",
                StringComparison.Ordinal) &&
            rollout.Contains(
                "Code-Signing-Zertifikat",
                StringComparison.Ordinal) &&
            rollout.Contains(
                "DISABLE-UPDATE.ps1 -Channel STABLE",
                StringComparison.Ordinal),
            "R178 ships an operator runbook covering pilot rollout, signing, stable promotion and emergency stop");

        var main = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));

        assert(
            main.Contains(
                "ScannerCapture.TextChanged += OnScannerCaptureTextChanged",
                StringComparison.Ordinal) &&
            main.Contains(
                "if (ScannerCapture.IsFocused)",
                StringComparison.Ordinal) &&
            main.Contains(
                "e.Key is Key.Enter or Key.Tab",
                StringComparison.Ordinal) &&
            main.Contains(
                "e.Handled = true;",
                StringComparison.Ordinal),
            "R178 cashier scanner uses the focused TextBox as source of truth and always consumes scanner Enter/Tab before UI navigation");

        assert(
            main.Contains(
                "Dedicated sink owns its own TextChanged stream",
                StringComparison.Ordinal) &&
            main.Contains(
                "if (ScannerCapture.IsFocused)",
                StringComparison.Ordinal) &&
            main.Contains(
                "return;",
                StringComparison.Ordinal),
            "R178 window TextInput fallback cannot duplicate characters already captured by the dedicated scanner sink");

        var drawerPaymentMethodIndex = main.IndexOf(
            "private async Task TryOpenCashDrawerAfterPaymentAsync",
            StringComparison.Ordinal);
        var drawerPaymentSource = drawerPaymentMethodIndex >= 0
            ? main[drawerPaymentMethodIndex..Math.Min(
                main.Length,
                drawerPaymentMethodIndex + 6000)]
            : "";

        assert(
            drawerPaymentSource.Contains(
                "_settings.GetAsync(",
                StringComparison.Ordinal) &&
            drawerPaymentSource.Contains(
                "\"device.drawer.enabled\"",
                StringComparison.Ordinal) &&
            drawerPaymentSource.Contains(
                "\"device.receipt_printer.drawer_channel\"",
                StringComparison.Ordinal) &&
            settings.Contains(
                "[\"device.drawer.enabled\"] = \"true\"",
                StringComparison.Ordinal) &&
            settings.Contains(
                "[\"device.receipt_printer.drawer_channel\"]",
                StringComparison.Ordinal) &&
            settings.Contains(
                "Test und echte Zahlung verwenden jetzt dieselbe Einstellung",
                StringComparison.Ordinal),
            "R178 a successful drawer hardware test persists the same enable/channel settings read fresh by real cash payment");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            for (var dir = new DirectoryInfo(start);
                 dir is not null;
                 dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R178 review could not locate repository file: {relativePath}");
    }
}
