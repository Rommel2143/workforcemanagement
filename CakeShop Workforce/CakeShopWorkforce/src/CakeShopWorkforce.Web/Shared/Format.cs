using System.Globalization;

namespace CakeShopWorkforce.Web.Shared;

/// <summary>
/// One place for how numbers, money, dates and times look on screen.
/// If the client wants "PHP 1,000" instead of "₱1,000", change it here only.
/// </summary>
public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Money(decimal value) => "₱" + value.ToString("N2", Inv);

    public static string MoneyShort(decimal value) => value switch
    {
        >= 1_000_000 => "₱" + (value / 1_000_000m).ToString("0.##", Inv) + "M",
        >= 10_000 => "₱" + (value / 1_000m).ToString("0.#", Inv) + "K",
        _ => "₱" + value.ToString("N0", Inv)
    };

    public static string Number(decimal value, int decimals = 0) => value.ToString("N" + decimals, Inv);

    public static string Number(int value) => value.ToString("N0", Inv);

    /// <summary>Shows "—" when there is no value (for example: dividing by zero people).</summary>
    public static string OrDash(decimal? value, int decimals = 1) => value is null ? "—" : value.Value.ToString("N" + decimals, Inv);

    public static string Percent(decimal? value) => value is null ? "—" : value.Value.ToString("0", Inv) + "%";

    public static string Hours(decimal hours) => hours.ToString("0.#", Inv) + " h";

    public static string Time(TimeSpan? time) =>
        time is null ? "—" : DateTime.Today.Add(time.Value).ToString("h:mm tt", Inv);

    /// <summary>Compact time for calendar chips: 6a, 3:30p</summary>
    public static string TimeShort(TimeSpan? time)
    {
        if (time is null) return "";
        var t = DateTime.Today.Add(time.Value);
        var suffix = t.Hour < 12 ? "a" : "p";
        var hour = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return t.Minute == 0 ? $"{hour}{suffix}" : $"{hour}:{t.Minute:00}{suffix}";
    }

    public static string TimeRange(TimeSpan? start, TimeSpan? end) => $"{TimeShort(start)}–{TimeShort(end)}";

    public static string Date(DateTime date) => date.ToString("ddd, MMM d", Inv);

    public static string DateLong(DateTime date) => date.ToString("dddd, MMMM d, yyyy", Inv);

    public static string DateIso(DateTime date) => date.ToString("yyyy-MM-dd", Inv);

    public static string HourLabel(int hour) => hour switch
    {
        0 => "12 AM",
        < 12 => $"{hour} AM",
        12 => "12 PM",
        _ => $"{hour - 12} PM"
    };

    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }
}

/// <summary>Small date helpers. Weeks start on Monday.</summary>
public static class Dates
{
    public static readonly string[] DayCodes = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static DateTime StartOfWeek(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-daysSinceMonday);
    }

    public static DateTime StartOfMonth(DateTime date) => new(date.Year, date.Month, 1);

    public static string DayCode(DateTime date) => DayCodes[((int)date.DayOfWeek + 6) % 7];

    public static IEnumerable<DateTime> Range(DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) yield return d;
    }
}
