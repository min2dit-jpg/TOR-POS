using TorPos.App;
using TorPos.Infrastructure;

public static class EditionSplitFoundationTests
{
    public static Task Run(Action<bool,string> assert)
    {
        var original = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        try
        {
            assert(
                ProductBuild.FixedEdition is null,
                "split foundation keeps the default safety build on the legacy shared product identity");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "KIOSK");
            assert(
                AppPaths.ProductEdition == "KIOSK",
                "split foundation recognizes the fixed KIOSK product edition");
            assert(
                AppPaths.ProductDataDirectoryName() == "TOR-KIOSK",
                "TOR KIOSK receives its own AppData root");
            assert(
                AppPaths.TrialIdentityDirectory.EndsWith("TOR-KIOSK", StringComparison.Ordinal),
                "TOR KIOSK receives its own machine-wide licence/trial identity root");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "IMBISS");
            assert(
                AppPaths.ProductEdition == "IMBISS",
                "split foundation recognizes the fixed IMBISS product edition");
            assert(
                AppPaths.ProductDataDirectoryName() == "TOR-DOENER",
                "TOR DÖNER receives its own AppData root");
            assert(
                AppPaths.TrialIdentityDirectory.EndsWith("TOR-DOENER", StringComparison.Ordinal),
                "TOR DÖNER receives its own machine-wide licence/trial identity root");

            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "invalid");
            assert(
                AppPaths.ProductEdition is null &&
                AppPaths.ProductDataDirectoryName() == "TOR-POS-Pro",
                "unknown product identity fails back to the legacy shared R181 data root");

            var project = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/TorPos.App.csproj"));
            assert(
                project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'KIOSK'\">TOR-KIOSK</AssemblyName>", StringComparison.Ordinal) &&
                project.Contains("<AssemblyName Condition=\"'$(TorProductEdition)' == 'IMBISS'\">TOR-DOENER</AssemblyName>", StringComparison.Ordinal) &&
                project.Contains("TOR_KIOSK_PRODUCT", StringComparison.Ordinal) &&
                project.Contains("TOR_DOENER_PRODUCT", StringComparison.Ordinal),
                "one audited App project emits distinct TOR KIOSK and TOR DÖNER binaries");

            var program = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/Program.cs"));
            var app = File.ReadAllText(
                FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));
            assert(
                program.Contains("ProductBuild.ConfigureEnvironment()", StringComparison.Ordinal) &&
                program.Contains("ProductBuild.RunningMutexName", StringComparison.Ordinal) &&
                app.Contains("var builtEdition = ProductBuild.FixedEdition;", StringComparison.Ordinal) &&
                app.Contains("return builtEdition;", StringComparison.Ordinal),
                "split builds fix both process identity and login edition before normal application flow");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", original);
        }

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
