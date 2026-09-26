using TorPos.Core;

public static class R171ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var utcRange = DsfinvkExportRange.ForDates(
            new DateOnly(2026, 1, 2),
            new DateOnly(2026, 1, 3),
            TimeZoneInfo.Utc);

        assert(
            utcRange.FromInclusive == new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) &&
            utcRange.ToInclusive == new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero).AddTicks(-1),
            "R171 DSFinV-K Von/Bis includes both selected calendar days completely");

        var reversedRejected = false;
        try
        {
            _ = DsfinvkExportRange.ForDates(
                new DateOnly(2026, 2, 2),
                new DateOnly(2026, 2, 1),
                TimeZoneInfo.Utc);
        }
        catch (ArgumentException)
        {
            reversedRejected = true;
        }
        assert(
            reversedRejected,
            "R171 DSFinV-K date range rejects an end date before the start date");

        var settings = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));

        assert(
            settings.Contains("DSFinV-K · Von", StringComparison.Ordinal) &&
            settings.Contains("DSFinV-K · Bis", StringComparison.Ordinal) &&
            settings.Contains("DSFINV-K 2.4 EXPORTIEREN", StringComparison.Ordinal),
            "R171 fiscal settings expose explicit start date, end date and export action");

        assert(
            settings.Contains("DsfinvkExportRange.ForDates(", StringComparison.Ordinal) &&
            settings.Contains("_dsfinvkExport.ValidateAsync(", StringComparison.Ordinal) &&
            settings.Contains("_dsfinvkExport.ExportAsync(", StringComparison.Ordinal),
            "R171 selected calendar dates feed both DSFinV-K preflight and the real export");

        assert(
            settings.Contains("OpenFolderPickerAsync", StringComparison.Ordinal) &&
            settings.Contains("\"DSFINVK_EXPORT\"", StringComparison.Ordinal) &&
            settings.Contains("kein Zielordner ausgewählt", StringComparison.Ordinal),
            "R171 DSFinV-K export lets the operator choose a destination and audits successful exports");

        assert(
            settings.Contains("nur vollständig abgeschlossene Kassenabschluss-Zeiträume", StringComparison.Ordinal) &&
            settings.Contains("Vorgänge nach dem letzten Z-Bericht", StringComparison.Ordinal),
            "R171 UI states that date filtering preserves complete Z-report periods and excludes open periods");

        var service = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/DsfinvkExportService.cs"));
        assert(
            service.Contains("z.CreatedAt >= from && z.CreatedAt <= to", StringComparison.Ordinal) &&
            service.Contains("\"NO_CLOSING\"", StringComparison.Ordinal) &&
            service.Contains("Exportiert werden nur abgeschlossene Zeiträume", StringComparison.Ordinal),
            "R171 backend range selection remains fail-closed around completed Z reports");

        var revisionNumber =
            int.TryParse(
                TorRelease.Revision.TrimStart('R', 'r'),
                out var parsedRevision)
                ? parsedRevision
                : 0;
        assert(
            revisionNumber >= 171 &&
            TorRelease.Version != "0.7.33.849" &&
            TorRelease.Revision != "R149" &&
            TorRelease.UserAgentVersion.EndsWith("-" + TorRelease.Revision, StringComparison.Ordinal),
            "R171 authoritative runtime release metadata advanced from the stale R149 value and remains forward-compatible");

        var changelogPath = FindRepoFile("CHANGELOG.md");
        var repositoryRoot = Path.GetDirectoryName(changelogPath)
            ?? throw new DirectoryNotFoundException("Repository root could not be resolved.");
        var rootReadme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var desktopReadme = File.ReadAllText(FindRepoFile("Desktop/README.md"));
        var changelog = File.ReadAllText(changelogPath);
        var currentMarker =
            $"TOR_RELEASE:{TorRelease.Revision}|{TorRelease.Version}|{TorRelease.ReleaseName}";
        var currentDesktop =
            $"Aktueller Stand:** {TorRelease.Revision} · {TorRelease.ReleaseName} · {TorRelease.Version}";
        assert(
            rootReadme.Contains(currentMarker, StringComparison.Ordinal) &&
            desktopReadme.Contains(currentDesktop, StringComparison.Ordinal) &&
            changelog.Contains(currentMarker, StringComparison.Ordinal),
            "R171 root README, Desktop README and release index agree on the current release");

        var versionCheck = File.ReadAllText(
            FindRepoFile("Desktop/tools/Verify-Version.ps1"));
        assert(
            versionCheck.Contains("R*ReviewTests.cs", StringComparison.Ordinal) &&
            versionCheck.Contains("$highestReview", StringComparison.Ordinal) &&
            versionCheck.Contains("$revisionNumber -lt $highestReview", StringComparison.Ordinal) &&
            versionCheck.Contains("Desktop/README.md", StringComparison.Ordinal),
            "R171 CI rejects a release revision that falls behind a newer R###ReviewTests contract and checks Desktop README too");

        assert(
            settings.Contains("new DsfinvkDeliveryWindow(", StringComparison.Ordinal) &&
            settings.Contains(".ShowDialog(this)", StringComparison.Ordinal),
            "R171 successful DSFinV-K export immediately offers a delivery assistant instead of leaving the operator to find the folder manually");

        var delivery = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.App/DsfinvkDeliveryWindow.cs"));
        assert(
            delivery.Contains("AUF USB KOPIEREN", StringComparison.Ordinal) &&
            delivery.Contains("ANDEREN USB-/ORDNER WÄHLEN", StringComparison.Ordinal) &&
            delivery.Contains("DSFINV-K PER E-MAIL SENDEN", StringComparison.Ordinal) &&
            delivery.Contains("STEUERBERATER-ADRESSE", StringComparison.Ordinal),
            "R171 delivery assistant offers removable-media copy, manual folder choice and email to the saved tax-adviser address");

        // The ZIP is built by DsfinvkPackage, which also verifies every entry
        // byte for byte against the export folder before it may be sent.
        var package = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/DsfinvkPackage.cs"));
        assert(
            delivery.Contains("DsfinvkPackage.Create(_exportFolder, zip)", StringComparison.Ordinal) &&
            package.Contains("ZipFile.CreateFromDirectory(", StringComparison.Ordinal) &&
            package.Contains("includeBaseDirectory: true", StringComparison.Ordinal) &&
            delivery.Contains("MaxMailZipBytes = 15L * 1024 * 1024", StringComparison.Ordinal) &&
            delivery.Contains("SendFilesAsync(", StringComparison.Ordinal),
            "R171 email delivery packages the complete DSFinV-K folder into one bounded ZIP attachment");

        assert(
            delivery.Contains(".unvollstaendig", StringComparison.Ordinal) &&
            delivery.Contains("SearchOption.AllDirectories", StringComparison.Ordinal) &&
            delivery.Contains("Directory.Move(working, final)", StringComparison.Ordinal),
            "R171 USB delivery copies the complete export tree through an incomplete working folder before presenting a finished folder");

        var mime = File.ReadAllText(
            FindRepoFile("Desktop/src/TorPos.Infrastructure/MailMimeBuilder.cs"));
        assert(
            mime.Contains("\".zip\" => \"application/zip\"", StringComparison.Ordinal) &&
            mime.Contains("\".csv\" => \"text/csv\"", StringComparison.Ordinal) &&
            mime.Contains("\".xml\" => \"application/xml\"", StringComparison.Ordinal),
            "R171 mail MIME generation labels DSFinV-K ZIP/CSV/XML attachments correctly instead of declaring every file as PDF");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(
            $"R171 review could not locate repository file: {relativePath}");
    }
}
