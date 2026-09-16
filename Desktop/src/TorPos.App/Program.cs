using Avalonia;

namespace TorPos.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // R66: fixed process mutex lets the installer detect an open TOR POS
        // immediately instead of waiting on Restart Manager/file locks.
        using var installerGuard = new Mutex(true, "TOR-POS-Pro-Running", out var installerGuardOwner);
        if (!installerGuardOwner) return;

        using var instance = new Mutex(true, "TOR-POS-Pro-" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))))[..20], out var firstInstance);
        if (!firstInstance) return;
        CrashLog.ResetForNewProcess();
        CrashLog.InitializeGlobalHandlers();
        CrashLog.Write("Avalonia bootstrap starting.");

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Fatal bootstrap failure.", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
