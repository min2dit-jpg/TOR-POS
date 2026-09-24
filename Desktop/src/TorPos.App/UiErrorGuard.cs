using Avalonia.Threading;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// O-6: last line of defence for exceptions that escape a UI event handler.
/// Around 90 async void handlers exist; an exception in any of them used to
/// end the whole process. The error is logged with a Fehler-ID, the cashier
/// sees a short notice, and the till keeps running - unless
/// <see cref="UnhandledUiErrorPolicy"/> says the process is broken for good.
/// Checkout, TSE and payment code keep their own, specific error handling;
/// this only catches what nothing else caught.
/// </summary>
internal static class UiErrorGuard
{
    private static readonly UnhandledUiErrorPolicy Policy = new();
    private static bool _installed;

    /// <summary>Raised with the Fehler-ID after an error was caught.</summary>
    public static event Action<string>? ErrorCaught;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var now = DateTimeOffset.Now;
        var errorId = UnhandledUiErrorPolicy.NewErrorId(now);
        var catchIt = Policy.ShouldCatch(e.Exception, now);
        CrashLog.WriteException(
            $"ERROR-ID={errorId}; CATEGORY=UI; Unhandled UI exception; caught={catchIt}",
            e.Exception);
        if (!catchIt)
            return;

        e.Handled = true;
        try
        {
            ErrorCaught?.Invoke(errorId);
        }
        catch (Exception notifyError)
        {
            CrashLog.WriteException("UI error notice failed", notifyError);
        }
    }
}
