using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TorPos.Infrastructure;

public sealed record TseClockUpdateAssessment(
    bool Allowed,
    string Message);

/// <summary>
/// Pure policy for deciding whether the Windows UTC clock is credible enough
/// to be written into a Swissbit TSE.
/// </summary>
public static class TseClockUpdatePolicy
{
    public static readonly TimeSpan RuntimeDriftTolerance =
        TimeSpan.FromMinutes(5);

    public static TseClockUpdateAssessment Assess(
        DateTimeOffset candidateUtc,
        DateTimeOffset? lastTseLogTimeUtc,
        DateTimeOffset? monotonicExpectedUtc = null)
    {
        var candidate = candidateUtc.ToUniversalTime();
        var last = lastTseLogTimeUtc?.ToUniversalTime();

        if (last is not null && candidate < last.Value)
        {
            return new(
                false,
                "TSE-Zeitaktualisierung gesperrt: Windows-Zeit liegt vor dem letzten TSE-Log rückwärts. Systemzeit prüfen.");
        }

        if (monotonicExpectedUtc is { } expected)
        {
            var drift =
                (candidate - expected.ToUniversalTime()).Duration();

            if (drift > RuntimeDriftTolerance)
            {
                return new(
                    false,
                    $"TSE-Zeitaktualisierung gesperrt: Windows-Zeit weicht um {drift.TotalMinutes:0.0} Minuten vom laufzeitbasierten Erwartungswert ab. Systemzeit/NTP prüfen.");
            }
        }

        return new(true, "TSE-Zeitquelle plausibel.");
    }
}

/// <summary>
/// Runtime clock-safety state. A persisted TSE log timestamp is used as the
/// fail-closed lower bound after startup. Once a real TSE result is observed in
/// this process, Stopwatch supplies a monotonic reference so later host-clock
/// jumps larger than five minutes are rejected without relying on wall time.
/// </summary>
public sealed class TseClockSafetyState
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastTseLogTimeUtc;
    private long? _observedStopwatchTimestamp;

    public DateTimeOffset? LastTseLogTimeUtc
    {
        get
        {
            lock (_gate)
                return _lastTseLogTimeUtc;
        }
    }

    public void Seed(DateTimeOffset? logTime)
    {
        lock (_gate)
        {
            _lastTseLogTimeUtc =
                logTime?.ToUniversalTime();
            _observedStopwatchTimestamp = null;
        }
    }

    public void Observe(DateTimeOffset? logTime)
    {
        if (logTime is null)
            return;

        lock (_gate)
        {
            var value = logTime.Value.ToUniversalTime();
            if (_lastTseLogTimeUtc is null ||
                value >= _lastTseLogTimeUtc.Value)
            {
                _lastTseLogTimeUtc = value;
                _observedStopwatchTimestamp =
                    Stopwatch.GetTimestamp();
            }
        }
    }

    public TseClockUpdateAssessment Assess(
        DateTimeOffset candidateUtc)
    {
        lock (_gate)
        {
            DateTimeOffset? expected = null;

            if (_lastTseLogTimeUtc is { } last &&
                _observedStopwatchTimestamp is { } observed)
            {
                expected =
                    last + Stopwatch.GetElapsedTime(observed);
            }

            return TseClockUpdatePolicy.Assess(
                candidateUtc,
                _lastTseLogTimeUtc,
                expected);
        }
    }

    public static async Task<TseClockSafetyState> LoadAsync(
        SqliteDatabase db,
        CancellationToken ct = default)
    {
        var state = new TseClockSafetyState();
        DateTimeOffset? latest = null;

        await using var c = db.OpenReadConnection();

        foreach (var candidate in CandidateColumns)
        {
            ct.ThrowIfCancellationRequested();

            if (!await HasColumnAsync(
                    c,
                    candidate.Table,
                    candidate.Column,
                    ct))
            {
                continue;
            }

            await using var q = c.CreateCommand();
            q.CommandText =
                $"SELECT {candidate.Column} FROM {candidate.Table} " +
                $"WHERE TRIM(COALESCE({candidate.Column},''))<>'' " +
                $"ORDER BY {candidate.Column} DESC LIMIT 1;";

            var raw = Convert.ToString(
                await q.ExecuteScalarAsync(ct));

            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                var utc = parsed.ToUniversalTime();
                if (latest is null || utc > latest.Value)
                    latest = utc;
            }
        }

        state.Seed(latest);
        return state;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection c,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var exists = c.CreateCommand();
        exists.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table;";
        exists.Parameters.AddWithValue("$table", table);

        if (Convert.ToInt32(
                await exists.ExecuteScalarAsync(ct)) == 0)
        {
            return false;
        }

        await using var q = c.CreateCommand();
        q.CommandText = $"PRAGMA table_info({table});";
        await using var r = await q.ExecuteReaderAsync(ct);

        while (await r.ReadAsync(ct))
        {
            if (string.Equals(
                    r.GetString(1),
                    column,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly (string Table, string Column)[]
        CandidateColumns =
        [
            ("sale_tse_signatures", "log_time"),
            ("training_tse_signatures", "log_time"),
            ("cash_movement_tse_signatures", "log_time"),
            ("parked_receipts", "tse_log_time"),
            ("order_bestellungen", "log_time"),
            ("aborted_vorgaenge", "log_time"),
            ("restaurant_bestellungen", "log_time")
        ];
}
