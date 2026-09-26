namespace TorPos.Core;

/// <summary>
/// Additional DSFinV-K preflight rules that need no database: the selected
/// period, the continuity of the Z numbers and the TSE periods inside the
/// export. A blocking issue stops the export; a warning is shown and written
/// into the export protocol - never hidden behind a "successful" message.
/// </summary>
public static class DsfinvkPreflightChecks
{
    public static IReadOnlyList<DsfinvkPreflightIssue> Range(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        var issues = new List<DsfinvkPreflightIssue>();
        if (to < from)
            return issues; // reported as RANGE by the export itself

        timeZone ??= TimeZoneInfo.Local;
        var fromLocal = TimeZoneInfo.ConvertTime(from, timeZone);
        var toLocal = TimeZoneInfo.ConvertTime(to, timeZone);
        var whole = DsfinvkExportRange.ForDates(
            DateOnly.FromDateTime(fromLocal.DateTime),
            DateOnly.FromDateTime(toLocal.DateTime),
            timeZone);

        if (whole.FromInclusive != from || whole.ToInclusive != to)
            issues.Add(new("RANGE_TEILTAG",
                "Der Zeitraum beginnt oder endet nicht an einer Tagesgrenze. Exportiert werden nur Kassenabschlüsse, deren Z-Bericht im Zeitraum liegt.",
                Blocking: false));

        if (to > now)
            issues.Add(new("RANGE_ZUKUNFT",
                "Der Zeitraum reicht in die Zukunft. Enthalten sind nur bisher abgeschlossene Z-Berichte.",
                Blocking: false));

        return issues;
    }

    /// <summary>
    /// Z numbers must run without gaps or duplicates up to the last closing of
    /// the export. A gap means a missing Kassenabschluss - the export would be
    /// incomplete without anybody noticing.
    /// </summary>
    public static IReadOnlyList<DsfinvkPreflightIssue> ClosingContinuity(
        IReadOnlyList<long> allZNumbers,
        long lastZNumberInRange)
    {
        var issues = new List<DsfinvkPreflightIssue>();
        var relevant = allZNumbers.Where(z => z <= lastZNumberInRange).OrderBy(z => z).ToList();

        var duplicates = relevant.GroupBy(z => z).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            issues.Add(new("Z_DOPPELT", $"Z_NR mehrfach vorhanden: {string.Join(", ", duplicates)}. Export gesperrt."));

        var gaps = new List<string>();
        var distinct = relevant.Distinct().ToList();
        for (var i = 1; i < distinct.Count; i++)
        {
            if (distinct[i] != distinct[i - 1] + 1)
                gaps.Add(distinct[i] - distinct[i - 1] == 2
                    ? $"{distinct[i - 1] + 1}"
                    : $"{distinct[i - 1] + 1}-{distinct[i] - 1}");
        }

        if (gaps.Count > 0)
            issues.Add(new("Z_LUECKE", $"Fehlende Kassenabschlüsse (Z_NR {string.Join(", ", gaps)}). Der Export enthält diese Lücke - Datenbestand und Verfahrensdokumentation prüfen.", Blocking: false));

        return issues;
    }

    /// <summary>
    /// Every TSE used in the period, in order of first use. More than one TSE is
    /// legitimate (TSE-Wechsel) - each keeps its own Stamm_TSE row - but each
    /// transition should be documented in the TSE change journal.
    /// </summary>
    public static IReadOnlyList<DsfinvkPreflightIssue> TsePeriods(
        IReadOnlyList<string> serialsInOrderOfFirstUse,
        IReadOnlyCollection<(string Previous, string Next)> documentedChanges)
    {
        var issues = new List<DsfinvkPreflightIssue>();
        var serials = serialsInOrderOfFirstUse
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (serials.Count <= 1)
            return issues;

        issues.Add(new("TSE_PERIODEN",
            $"Im Zeitraum wurden {serials.Count} TSE verwendet ({string.Join(" → ", serials)}). Jede TSE erscheint mit eigenen Stammdaten; bisherige Vorgänge bleiben ihrer TSE zugeordnet.",
            Blocking: false));

        var undocumented = new List<string>();
        for (var i = 1; i < serials.Count; i++)
        {
            var documented = documentedChanges.Any(c =>
                string.Equals(c.Previous, serials[i - 1], StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Next, serials[i], StringComparison.OrdinalIgnoreCase));
            if (!documented)
                undocumented.Add($"{serials[i - 1]} → {serials[i]}");
        }

        if (undocumented.Count > 0)
            issues.Add(new("TSE_WECHSEL_UNDOKUMENTIERT",
                $"TSE-Wechsel ohne Eintrag im TSE-Wechselprotokoll: {string.Join("; ", undocumented)}. Für die Verfahrensdokumentation bitte Grund und Datum nachtragen.",
                Blocking: false));

        return issues;
    }
}
