using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TorPos.App;

public static class CrashLog
{
    private static readonly object Sync = new();
    private static bool _handlersInitialized;
    private static string? _sessionLogPath;

    // R182: each product logs under its own name. ProductBuild.ProductName is
    // "TOR POS Pro" in the shared build, so existing installations keep their path.
    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductBuild.ProductName,
            "Logs");

    private static string RunningMarkerPath =>
        Path.Combine(LogDirectory, "TOR-POS.running");

    private static string LatestLogPath =>
        Path.Combine(LogDirectory, "TOR-POS-latest.log");

    public static string LogPath =>
        _sessionLogPath ?? LatestLogPath;

    public static bool PreviousRunEndedUnexpectedly { get; private set; }

    public static void InitializeGlobalHandlers()
    {
        lock (Sync)
        {
            if (_handlersInitialized)
                return;

            _handlersInitialized = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteException(
                    $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}.",
                    ex);
            else
                Write(
                    $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}. " +
                    args.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // This handler is diagnostic only. We do not try to continue a cashier
            // operation after an arbitrary unhandled exception. Marking an
            // unobserved background task as observed merely prevents duplicate
            // escalation by the finalizer thread after it has already been logged.
            WriteException("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    public static void Write(string text)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var line = $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);

                // Keep one predictable path for service/support while preserving
                // the immutable per-process session log above.
                if (!string.Equals(LogPath, LatestLogPath, StringComparison.OrdinalIgnoreCase))
                    File.AppendAllText(LatestLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never stop the POS from starting or closing.
        }
    }

    public static void WriteException(string context, Exception ex) =>
        Write($"{context}{Environment.NewLine}{ex}");

    public static void ResetForNewProcess()
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);

                PreviousRunEndedUnexpectedly = File.Exists(RunningMarkerPath);

                var now = DateTimeOffset.Now;
                var stamp = now.ToString("yyyyMMdd-HHmmss");
                _sessionLogPath = Path.Combine(
                    LogDirectory,
                    $"TOR-POS-{stamp}-P{Environment.ProcessId}.log");

                var header =
                    $"TOR POS Pro session log{Environment.NewLine}" +
                    $"Started: {now:O}{Environment.NewLine}" +
                    $"Process: {Environment.ProcessId}{Environment.NewLine}" +
                    $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                    $".NET: {Environment.Version}{Environment.NewLine}" +
                    $"64-bit process: {Environment.Is64BitProcess}{Environment.NewLine}" +
                    $"Previous run unclean: {PreviousRunEndedUnexpectedly}{Environment.NewLine}" +
                    new string('-', 72) + Environment.NewLine;

                File.WriteAllText(_sessionLogPath, header, Encoding.UTF8);
                File.WriteAllText(LatestLogPath, header, Encoding.UTF8);

                if (PreviousRunEndedUnexpectedly)
                {
                    var oldMarker = File.ReadAllText(RunningMarkerPath, Encoding.UTF8);
                    var warning =
                        $"[{now:O}] Previous TOR POS process did not record a clean shutdown." +
                        Environment.NewLine + oldMarker + Environment.NewLine;
                    File.AppendAllText(_sessionLogPath, warning, Encoding.UTF8);
                    File.AppendAllText(LatestLogPath, warning, Encoding.UTF8);
                }

                File.WriteAllText(
                    RunningMarkerPath,
                    $"Started={now:O}{Environment.NewLine}" +
                    $"ProcessId={Environment.ProcessId}{Environment.NewLine}" +
                    $"Log={_sessionLogPath}{Environment.NewLine}",
                    Encoding.UTF8);

                PruneOldSessionLogs(maxSessionLogs: 30);
            }
        }
        catch
        {
            // Startup diagnostics are best-effort only.
        }
    }

    public static void MarkCleanShutdown()
    {
        try
        {
            lock (Sync)
            {
                var line =
                    $"[{DateTimeOffset.Now:O}] Clean application shutdown recorded." +
                    Environment.NewLine;

                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
                if (!string.Equals(LogPath, LatestLogPath, StringComparison.OrdinalIgnoreCase))
                    File.AppendAllText(LatestLogPath, line, Encoding.UTF8);

                if (File.Exists(RunningMarkerPath))
                    File.Delete(RunningMarkerPath);
            }
        }
        catch
        {
            // Never block Windows shutdown because a diagnostic marker failed.
        }
    }

    // R85: lets a technician look up the full technical detail (exception
    // type, message, stack trace) behind a Fehler-ID a cashier was shown,
    // without opening raw log files by hand. Searches newest-first across
    // every session log plus the rolling latest log, since the error may
    // have happened in an earlier session.
    public static string? FindErrorId(string errorId)
    {
        if (string.IsNullOrWhiteSpace(errorId))
            return null;

        var needle = "ERROR-ID=" + errorId.Trim();

        try
        {
            if (!Directory.Exists(LogDirectory))
                return null;

            var files = new DirectoryInfo(LogDirectory)
                .GetFiles("TOR-POS-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file.FullName, Encoding.UTF8); }
                catch { continue; }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var block = new List<string> { lines[i] };
                    for (var j = i + 1; j < lines.Length && !lines[j].StartsWith('['); j++)
                        block.Add(lines[j]);

                    return $"Datei: {file.Name}{Environment.NewLine}{string.Join(Environment.NewLine, block)}";
                }
            }
        }
        catch
        {
            // A lookup failure must never crash the diagnostics window.
        }

        return null;
    }

    private static void PruneOldSessionLogs(int maxSessionLogs)
    {
        try
        {
            var files = new DirectoryInfo(LogDirectory)
                .GetFiles("TOR-POS-*-P*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(Math.Max(1, maxSessionLogs));

            foreach (var file in files)
            {
                try { file.Delete(); }
                catch { }
            }
        }
        catch
        {
        }
    }
}

public sealed class StartupLoadingWindow : Window
{
    public StartupLoadingWindow()
    {
        Title = "TOR Kassensysteme";
        Width = 680;
        Height = 430;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.Parse("#F4F7FB"));

        var logo = CreateLogo();

        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(34, 26),
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8E2EE")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
                RowSpacing = 12,
                Children =
                {
                    logo,
                    CreateTitle(),
                    CreateStatus(),
                    CreateProgress(),
                    CreateFooter()
                }
            }
        };

        Grid.SetRow(logo, 0);
        Grid.SetRow(((Grid)((Border)Content).Child!).Children[1], 1);
        Grid.SetRow(((Grid)((Border)Content).Child!).Children[2], 2);
        Grid.SetRow(((Grid)((Border)Content).Child!).Children[3], 3);
        Grid.SetRow(((Grid)((Border)Content).Child!).Children[4], 4);
    }

    private static Control CreateLogo()
    {
        try
        {
            // R182: a dedicated TOR Einzelhandel / TOR Gastro build renames the assembly, and
            // avares URIs are keyed by assembly name. Derive it instead of hardcoding it.
            var assetAssembly = typeof(StartupLoadingWindow).Assembly.GetName().Name;
            var uri = new Uri($"avares://{assetAssembly}/Assets/TorPos-Brand.jpg");
            using var stream = AssetLoader.Open(uri);
            return new Border
            {
                Width = 150,
                Height = 150,
                CornerRadius = new CornerRadius(75),
                ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.Parse("#279CFF")),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Image
                {
                    Source = new Bitmap(stream),
                    Stretch = Stretch.UniformToFill
                }
            };
        }
        catch
        {
            // The splash must never block application startup. If the bundled
            // brand asset cannot be read, keep a neutral TOR-POS fallback.
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#07111F")),
                CornerRadius = new CornerRadius(75),
                Width = 150,
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "TOR-POS",
                    Foreground = Brushes.White,
                    FontSize = 28,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
    }

    private static Control CreateTitle() =>
        new StackPanel
        {
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "TOR POS",
                    Foreground = new SolidColorBrush(Color.Parse("#0B2A67")),
                    FontSize = 25,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = "Kassensystem wird vorbereitet ...",
                    Foreground = new SolidColorBrush(Color.Parse("#26364A")),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };

    private static Control CreateStatus() =>
        new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EEF6FF")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 11),
            Margin = new Thickness(20, 2),
            Child = new TextBlock
            {
                Text = "Einen kleinen Moment – TOR POS zählt schon mal bis drei. Gleich kann kassiert werden.",
                Foreground = new SolidColorBrush(Color.Parse("#34506E")),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }
        };

    private static Control CreateProgress() =>
        new ProgressBar
        {
            IsIndeterminate = true,
            Height = 8,
            Margin = new Thickness(54, 4, 54, 0)
        };

    private static Control CreateFooter() =>
        new TextBlock
        {
            Text = "Datenbank, Kasse und Sicherheit werden geladen.",
            Foreground = new SolidColorBrush(Color.Parse("#718197")),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };
}
