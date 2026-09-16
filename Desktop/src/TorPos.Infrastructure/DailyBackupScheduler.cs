using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Runs the database backup independently from Z reports, daily closing and cash-register closing.
/// A missed scheduled backup is caught up on the next application start. A successful backup is
/// never repeated on the same local calendar day. Failures are retried after five minutes.
/// </summary>
public sealed class DailyBackupScheduler : IAsyncDisposable
{
    private readonly DatabaseBackupService _backup;
    private readonly ISettingsRepository _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _loop;
    private DateTimeOffset? _retryNotBefore;

    public DailyBackupScheduler(
        DatabaseBackupService backup,
        ISettingsRepository settings,
        Func<DateTimeOffset>? now = null)
    {
        _backup = backup;
        _settings = settings;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = RunAsync(_stop.Token);
    }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var values = await _settings.LoadAllAsync(ct);
            if (!ReadBool(values, "backup.daily.enabled", true)) return;

            var now = _now();
            if (_retryNotBefore is { } retry && now < retry) return;

            var schedule = ParseTime(values.TryGetValue("backup.daily.time", out var rawTime) ? rawTime : "00:00");
            var lastSuccess = ParseTimestamp(values.TryGetValue("backup.daily.last_success", out var rawLast) ? rawLast : "");

            // Exactly one successful scheduled backup per local calendar day.
            if (lastSuccess is { } success && success.LocalDateTime.Date == now.LocalDateTime.Date)
                return;

            var due = MostRecentDue(now, schedule);
            if (lastSuccess is { } previous && previous >= due)
                return;

            try
            {
                var directory = values.TryGetValue("backup.directory", out var configured) ? configured : "";
                var path = await _backup.CreateBackupAsync(directory, ct);
                path = await new BackupEncryptionService(_settings).EncryptIfEnabledAsync(path, ct);
                var completed = _now();
                await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backup.daily.last_success"] = completed.ToString("O"),
                    ["backup.daily.last_error"] = "",
                    ["backup.daily.last_path"] = path
                }, ct);
                _retryNotBefore = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _retryNotBefore = now.AddMinutes(5);
                var message = ex.Message.Length > 700 ? ex.Message[..700] : ex.Message;
                try
                {
                    await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["backup.daily.last_error"] = $"{now:O} · {message}"
                    }, ct);
                }
                catch
                {
                    // The success marker is intentionally not changed on failure.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static DateTimeOffset MostRecentDue(DateTimeOffset now, TimeOnly time)
    {
        var localNow = now.LocalDateTime;
        var candidateLocal = localNow.Date.Add(time.ToTimeSpan());
        if (candidateLocal > localNow) candidateLocal = candidateLocal.AddDays(-1);
        var offset = TimeZoneInfo.Local.GetUtcOffset(candidateLocal);
        return new DateTimeOffset(candidateLocal, offset);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Immediate first check catches a backup missed while TOR POS was closed.
        while (!ct.IsCancellationRequested)
        {
            try { await CheckNowAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Keep scheduler alive; CheckNowAsync stores normal backup errors. */ }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static TimeOnly ParseTime(string? value)
    {
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var hour) && int.TryParse(parts[1], out var minute) && hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
            return new TimeOnly(hour, minute);
        return new TimeOnly(0, 0);
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _gate.Dispose();
    }
}
