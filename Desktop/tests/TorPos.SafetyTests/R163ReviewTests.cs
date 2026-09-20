public static class R163ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var installer = File.ReadAllText(FindRepoFile("Desktop/TOR-POS-Pro-Setup.iss"));
        var workflow = File.ReadAllText(FindRepoFile(".github/workflows/tor-pos-ci.yml"));
        var manifest = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/app.manifest"));

        assert(
            installer.Contains("Flags: nowait postinstall skipifsilent shellexec", StringComparison.Ordinal),
            "R163 Setup launches TOR POS through Windows ShellExecute so elevation/compatibility prompts are handled by Windows instead of CreateProcess error 740");

        assert(
            workflow.Contains("Kunden Setup icin standart Windows publish olustur", StringComparison.Ordinal) &&
            workflow.Contains("-p:PublishSingleFile=false", StringComparison.Ordinal) &&
            workflow.Contains("-o ci-results/setup-publish", StringComparison.Ordinal),
            "R163 customer installer uses a normal multi-file Windows publish instead of reusing the portable single-file bootstrapper");

        assert(
            workflow.Contains("Setup EXE manifestini dogrula", StringComparison.Ordinal) &&
            workflow.Contains("Windows Kits\\10\\bin", StringComparison.Ordinal) &&
            workflow.Contains("requestedExecutionLevel", StringComparison.Ordinal) &&
            workflow.Contains("asInvoker", StringComparison.Ordinal),
            "R163 CI extracts the installed EXE manifest and fails if asInvoker is missing");

        assert(
            workflow.Contains("/DMyPublishDir=ci-results\\setup-publish", StringComparison.Ordinal) &&
            !workflow.Contains("/DMyPublishDir=ci-results\\publish\" \"/DMyOutputBaseFilename=TOR-POS-Setup", StringComparison.Ordinal),
            "R163 Inno Setup packages the manifest-verified setup publish, not the portable ZIP publish");

        assert(
            manifest.Contains("requestedExecutionLevel level=\"asInvoker\"", StringComparison.Ordinal),
            "R163 application manifest explicitly declares asInvoker for normal cashier operation");

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

        throw new FileNotFoundException($"R163 review could not locate repository file: {relativePath}");
    }
}
