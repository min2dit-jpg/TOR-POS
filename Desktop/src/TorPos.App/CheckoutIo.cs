namespace TorPos.App;

/// <summary>
/// R65: helper for Windows/device operations whose underlying API can block even
/// when a CancellationToken is supplied. The caller stops waiting after the
/// configured limit; a late fault is observed so it cannot become unobserved.
/// </summary>
internal static class CheckoutIo
{
    internal static async Task<T> WaitBoundedAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            return await operation.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = operation.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}
