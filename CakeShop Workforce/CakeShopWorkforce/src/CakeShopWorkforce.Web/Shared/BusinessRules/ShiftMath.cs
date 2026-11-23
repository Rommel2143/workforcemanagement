namespace CakeShopWorkforce.Web.Shared.BusinessRules;

/// <summary>
/// Time arithmetic for shifts. Handles shifts that cross midnight
/// (e.g. 10 PM → 6 AM) by adding 24 hours when the end is before the start.
///
/// These are "pure" functions: no database, no UI. Easy to read and test.
/// </summary>
public static class ShiftMath
{
    /// <summary>Hours between start and end (6:00 → 15:00 = 9).</summary>
    public static decimal SpanHours(TimeSpan start, TimeSpan end)
    {
        var span = end - start;
        if (span <= TimeSpan.Zero) span += TimeSpan.FromHours(24);   // crosses midnight
        return (decimal)span.TotalHours;
    }

    /// <summary>Paid hours = span minus the unpaid meal break (6:00 → 15:00 with 60 min break = 8).</summary>
    public static decimal PaidHours(TimeSpan start, TimeSpan end, int breakMinutes) =>
        Math.Max(0, SpanHours(start, end) - breakMinutes / 60m);

    /// <summary>Is the person on duty at any point during clock hour "hour" (e.g. 14 = 2:00–2:59 PM)?</summary>
    public static bool CoversHour(TimeSpan start, TimeSpan end, int hour)
    {
        var s = start.TotalHours;
        var e = end.TotalHours;
        if (e <= s) e += 24;
        return Overlaps(hour) || Overlaps(hour + 24);

        bool Overlaps(int h) => s < h + 1 && e > h;
    }

    /// <summary>Hours of rest between one shift's end and the next day's start.</summary>
    public static decimal RestHoursBetween(TimeSpan previousEnd, TimeSpan previousStart, TimeSpan nextStart)
    {
        // previous shift ends "today" (or after midnight if it crossed midnight)
        var endHour = (decimal)previousEnd.TotalHours;
        if (previousEnd <= previousStart) endHour += 24;
        return 24 + (decimal)nextStart.TotalHours - endHour;
    }
}
