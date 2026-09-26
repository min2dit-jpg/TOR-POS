using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace TorPos.Infrastructure;

/// <summary>
/// G-4: licence facts that must survive deleting one user-writable file.
///
/// A deactivated licence ID is written as a tombstone to every store - the
/// till's data folder, the machine-wide ProgramData folder and (on Windows)
/// the user's registry - and counts as deactivated when ANY store has it, so
/// deleting commercial-license-deactivations.jsonl alone no longer brings a
/// returned licence back. The same stores keep the highest UTC time the till
/// has seen; licence expiry is judged against max(now, that time), so turning
/// the Windows clock back does not extend a licence.
///
/// This raises the bar for casual tampering; it does not claim to stop a
/// local administrator. Failure to write one store never blocks the till.
/// </summary>
public sealed class LicenseTamperStore
{
    private const string RegistryPath = @"Software\TOR-POS\LicenseState";
    private const string TombstonePrefix = "DEACTIVATED|";
    private const string HighWaterPrefix = "CLOCK|";

    public static readonly TimeSpan MaxForwardTrust = TimeSpan.FromDays(400);

    private readonly IReadOnlyList<string> _files;
    private readonly bool _useRegistry;

    public LicenseTamperStore(IEnumerable<string> files, bool useRegistry)
    {
        _files = files.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _useRegistry = useRegistry && OperatingSystem.IsWindows();
    }

    public static LicenseTamperStore Default() =>
        new(
            new[]
            {
                Path.Combine(AppPaths.DataDirectory, "license-state.dat"),
                Path.Combine(AppPaths.TrialIdentityDirectory, "license-state.dat")
            },
            useRegistry: true);

    public void RecordDeactivation(string licenseId)
    {
        licenseId = Normalize(licenseId);
        if (licenseId.Length == 0)
            return;
        foreach (var file in _files)
            TryAppend(file, TombstonePrefix + licenseId);
        if (_useRegistry)
            TryRegistry(key => key.SetValue("Deactivated-" + licenseId, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
    }

    public bool IsDeactivated(string licenseId)
    {
        licenseId = Normalize(licenseId);
        if (licenseId.Length == 0)
            return false;
        if (_files.Any(file => ReadLines(file).Contains(TombstonePrefix + licenseId, StringComparer.Ordinal)))
            return true;
        var inRegistry = false;
        if (_useRegistry)
            TryRegistry(key => inRegistry = key.GetValue("Deactivated-" + licenseId) is not null);
        return inRegistry;
    }

    /// <summary>
    /// The time a licence is judged at: never earlier than the latest time any
    /// store has seen. Moves the high-water mark forward, never back.
    /// </summary>
    public DateTimeOffset EffectiveUtcNow(DateTimeOffset utcNow)
    {
        var highWater = HighWater();
        // A clock that was once wrongly set far ahead must not expire a valid
        // licence for good: a high-water mark more than MaxForwardTrust ahead
        // is not trusted.
        var effective = highWater is { } seen && seen > utcNow && seen - utcNow <= MaxForwardTrust ? seen : utcNow;
        if (highWater is null || utcNow > highWater.Value)
        {
            var line = HighWaterPrefix + utcNow.UtcTicks.ToString(CultureInfo.InvariantCulture);
            foreach (var file in _files)
                TryAppend(file, line, keepLast: 200);
            if (_useRegistry)
                TryRegistry(key => key.SetValue("ClockHighWater", utcNow.UtcTicks.ToString(CultureInfo.InvariantCulture)));
        }

        return effective;
    }

    private DateTimeOffset? HighWater()
    {
        long best = 0;
        foreach (var file in _files)
        {
            foreach (var line in ReadLines(file))
            {
                if (line.StartsWith(HighWaterPrefix, StringComparison.Ordinal) &&
                    long.TryParse(line[HighWaterPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                    ticks > best && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                {
                    best = ticks;
                }
            }
        }

        if (_useRegistry)
        {
            TryRegistry(key =>
            {
                if (long.TryParse(key.GetValue("ClockHighWater") as string, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
                    ticks > best && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                {
                    best = ticks;
                }
            });
        }

        return best == 0 ? null : new DateTimeOffset(best, TimeSpan.Zero);
    }

    private static string Normalize(string? licenseId) =>
        new string((licenseId ?? "").Trim().ToUpperInvariant().Where(c => c is not ('\r' or '\n' or '|')).ToArray());

    private static IReadOnlyList<string> ReadLines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void TryAppend(string file, string line, int keepLast = 0)
    {
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            if (keepLast > 0)
            {
                // Clock lines are only a maximum; keep tombstones, trim old clock lines.
                var lines = ReadLines(file).ToList();
                var clock = lines.Where(x => x.StartsWith(HighWaterPrefix, StringComparison.Ordinal)).ToList();
                if (clock.Count >= keepLast)
                {
                    var keep = lines.Where(x => !x.StartsWith(HighWaterPrefix, StringComparison.Ordinal))
                        .Concat(clock.Skip(clock.Count - keepLast / 2))
                        .Append(line);
                    var temp = file + ".new";
                    File.WriteAllLines(temp, keep, new UTF8Encoding(false));
                    File.Move(temp, file, true);
                    return;
                }
            }

            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another store still holds the fact; the till is never blocked here.
        }
    }

    private static void TryRegistry(Action<RegistryKey> action)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            if (key is not null)
                action(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best effort, see TryAppend.
        }
    }
}
