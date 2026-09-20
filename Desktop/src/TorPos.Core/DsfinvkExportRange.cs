namespace TorPos.Core;

/// <summary>
/// Calendar-day selection used by the DSFinV-K operator UI. The selected
/// dates are inclusive. Each boundary uses the local time-zone offset valid
/// on that particular date, so a range crossing DST does not reuse today's
/// offset for the whole period.
/// </summary>
public sealed record DsfinvkExportRange(
    DateOnly FromDate,
    DateOnly ToDate,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToInclusive)
{
    public static DsfinvkExportRange ForDates(
        DateOnly from,
        DateOnly to,
        TimeZoneInfo? timeZone = null)
    {
        if (to < from)
            throw new ArgumentException("Enddatum darf nicht vor dem Startdatum liegen.", nameof(to));

        timeZone ??= TimeZoneInfo.Local;

        static DateTime Unspecified(DateOnly date) =>
            DateTime.SpecifyKind(
                date.ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Unspecified);

        var startLocal = Unspecified(from);
        var dayAfterLocal = Unspecified(to.AddDays(1));

        var start = new DateTimeOffset(
            startLocal,
            timeZone.GetUtcOffset(startLocal));

        var dayAfter = new DateTimeOffset(
            dayAfterLocal,
            timeZone.GetUtcOffset(dayAfterLocal));

        return new DsfinvkExportRange(
            from,
            to,
            start,
            dayAfter.AddTicks(-1));
    }
}
