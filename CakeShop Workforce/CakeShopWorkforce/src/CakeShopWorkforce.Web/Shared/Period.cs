namespace CakeShopWorkforce.Web.Shared;

/// <summary>A date range chosen with the PeriodPicker: Daily, Weekly, Monthly or Custom.</summary>
public sealed record Period(string Mode, DateTime From, DateTime To)
{
    public static Period Day(DateTime date) => new("Daily", date.Date, date.Date);

    public static Period Week(DateTime date)
    {
        var start = Dates.StartOfWeek(date);
        return new("Weekly", start, start.AddDays(6));
    }

    public static Period Month(DateTime date)
    {
        var start = Dates.StartOfMonth(date);
        return new("Monthly", start, start.AddMonths(1).AddDays(-1));
    }

    public static Period Custom(DateTime from, DateTime to) => new("Custom", from.Date, to.Date);

    public int Days => (To - From).Days + 1;

    public string Label => Mode switch
    {
        "Daily" => Format.DateLong(From),
        "Weekly" => $"{From:MMM d} – {To:MMM d, yyyy}",
        "Monthly" => From.ToString("MMMM yyyy"),
        _ => $"{From:MMM d, yyyy} – {To:MMM d, yyyy}"
    };
}
