namespace TorPos.Core;

/// <summary>
/// O-6: decides whether an exception that escaped a UI event handler (for
/// example an async void button click) is caught so the till keeps running,
/// or left to end the process.
///
/// A single failing menu click must not close the whole till in the middle of
/// a shift. But a process that is broken for good - out of memory, a corrupt
/// stack, or a handler failing over and over - must not limp on pretending to
/// work, so those still end the program (and are logged first).
/// </summary>
public sealed class UnhandledUiErrorPolicy
{
    public const int MaxCaughtPerWindow = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _sync = new();

    public bool ShouldCatch(Exception exception, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsFatal(exception))
            return false;

        lock (_sync)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > Window)
                _recent.Dequeue();
            if (_recent.Count >= MaxCaughtPerWindow)
                return false;
            _recent.Enqueue(now);
            return true;
        }
    }

    public static bool IsFatal(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is OutOfMemoryException or InsufficientExecutionStackException or
                AccessViolationException or System.Runtime.InteropServices.SEHException or
                BadImageFormatException or TypeInitializationException)
            {
                return true;
            }

            if (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsFatal))
                return true;
        }

        return false;
    }

    public static string NewErrorId(DateTimeOffset now) =>
        now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" +
        Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
}
