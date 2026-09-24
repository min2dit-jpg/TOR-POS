using TorPos.Core;

// O-6: an exception escaping a UI event handler (about 90 async void
// handlers) used to end the whole process. It is now logged and caught, the
// cashier sees a Fehler-ID, and only a process that is broken for good still
// ends.
public static class O6UiErrorGuardTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var policy = new UnhandledUiErrorPolicy();
        var t0 = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        assert(
            policy.ShouldCatch(new InvalidOperationException("Menü"), t0) &&
            policy.ShouldCatch(new NullReferenceException(), t0) &&
            !policy.ShouldCatch(new OutOfMemoryException(), t0) &&
            !policy.ShouldCatch(new AggregateException(new InvalidOperationException("a"), new OutOfMemoryException()), t0) &&
            !policy.ShouldCatch(new InvalidOperationException("wrapper", new InsufficientExecutionStackException()), t0),
            "O-6 an ordinary UI error is caught so the till keeps running, a fatal one (out of memory, broken stack) still ends the process");

        var storm = new UnhandledUiErrorPolicy();
        var caught = 0;
        for (var i = 0; i < UnhandledUiErrorPolicy.MaxCaughtPerWindow + 5; i++)
            if (storm.ShouldCatch(new InvalidOperationException("loop"), t0.AddSeconds(i)))
                caught++;
        assert(
            caught == UnhandledUiErrorPolicy.MaxCaughtPerWindow &&
            storm.ShouldCatch(new InvalidOperationException("later"), t0.AddMinutes(3)),
            "O-6 a handler failing over and over is not hidden forever - after 20 errors per minute the process ends, a later single error is caught again");

        var app = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/App.axaml.cs"));
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var guard = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UiErrorGuard.cs"));
        assert(
            app.Contains("UiErrorGuard.Install();", StringComparison.Ordinal) &&
            guard.Contains("Dispatcher.UIThread.UnhandledException += OnUnhandledException;", StringComparison.Ordinal) &&
            guard.Contains("CrashLog.WriteException(", StringComparison.Ordinal) &&
            guard.IndexOf("CrashLog.WriteException(", StringComparison.Ordinal) < guard.IndexOf("e.Handled = true;", StringComparison.Ordinal) &&
            main.Contains("UiErrorGuard.ErrorCaught += OnUiErrorCaught;", StringComparison.Ordinal) &&
            main.Contains("UiErrorGuard.ErrorCaught -= OnUiErrorCaught;", StringComparison.Ordinal),
            "O-6 the guard is installed at start-up, logs every error before catching it, and the till shows the Fehler-ID to the cashier");

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
