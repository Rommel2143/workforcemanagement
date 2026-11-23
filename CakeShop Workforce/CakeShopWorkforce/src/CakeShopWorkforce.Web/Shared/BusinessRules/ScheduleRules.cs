namespace CakeShopWorkforce.Web.Shared.BusinessRules;

/// <summary>One schedule cell, in the simple shape the rules need.</summary>
public sealed class PlannedDay
{
    public int EmployeeId { get; init; }
    public DateTime Date { get; init; }
    public string AssignmentType { get; init; } = "Shift";   // Shift, RestDay, Leave
    public TimeSpan? StartTime { get; init; }
    public TimeSpan? EndTime { get; init; }
    public int BreakMinutes { get; init; }
    public int? StationId { get; init; }

    public bool IsShift => AssignmentType == "Shift" && StartTime is not null && EndTime is not null;
    public decimal PaidHours => IsShift ? ShiftMath.PaidHours(StartTime!.Value, EndTime!.Value, BreakMinutes) : 0;
}

/// <summary>Employee facts the rules need.</summary>
public sealed class PlannedEmployee
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int MaxHoursPerWeek { get; init; }
    public string AvailableDays { get; init; } = "";
    public string? RestDay { get; init; }
    public bool IsActive { get; init; }
}

public sealed record ScheduleWarning(int? EmployeeId, DateTime? Date, string Severity, string Message);

/// <summary>
/// Checks a week's schedule and lists problems. Runs every time the schedule
/// page loads, so managers see issues BEFORE publishing.
///
///  Rule                               Why
///  ─────────────────────────────────  ──────────────────────────────────────────
///  Inactive employee scheduled        They no longer work here
///  7+ consecutive work days           PH Labor Code: 24h rest after 6 work days
///  Weekly hours above maximum         Employee's own limit (masterlist)
///  Daily paid hours above regular     Will create overtime
///  Not an available day               Masterlist "Available Working Days"
///  Clopening (< 10h rest)             Closing then opening the next morning
///  Station below minimum coverage     Uncovered station that day
/// </summary>
public static class ScheduleRules
{
    public const decimal MinimumRestHours = 10;

    public static List<ScheduleWarning> Check(
        DateTime weekStart,
        IReadOnlyList<PlannedDay> days,                 // this week + the 6 days before (for streaks)
        IReadOnlyList<PlannedEmployee> employees,
        IReadOnlyList<Station> stations,
        decimal regularHoursPerDay)
    {
        var warnings = new List<ScheduleWarning>();
        var weekEnd = weekStart.AddDays(6);
        var byEmployee = days.GroupBy(d => d.EmployeeId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.Date).ToList());

        foreach (var emp in employees)
        {
            if (!byEmployee.TryGetValue(emp.Id, out var empDays)) continue;
            var weekDays = empDays.Where(d => d.Date >= weekStart && d.Date <= weekEnd).ToList();

            // Inactive employee
            if (!emp.IsActive && weekDays.Any(d => d.IsShift))
                warnings.Add(new(emp.Id, null, "error", $"{emp.Name} is inactive but still has shifts this week."));

            // Weekly hours
            var weekHours = weekDays.Sum(d => d.PaidHours);
            if (weekHours > emp.MaxHoursPerWeek)
                warnings.Add(new(emp.Id, null, "warning", $"{emp.Name}: {weekHours:0.#} h scheduled, above the {emp.MaxHoursPerWeek} h weekly maximum."));

            foreach (var day in weekDays.Where(d => d.IsShift))
            {
                // Daily overtime
                if (day.PaidHours > regularHoursPerDay)
                    warnings.Add(new(emp.Id, day.Date, "info", $"{emp.Name}: {day.PaidHours:0.#} paid hours on {day.Date:ddd} → {day.PaidHours - regularHoursPerDay:0.#} h overtime."));

                // Availability
                if (!string.IsNullOrWhiteSpace(emp.AvailableDays) && !emp.AvailableDays.Contains(Dates.DayCode(day.Date)))
                    warnings.Add(new(emp.Id, day.Date, "warning", $"{emp.Name} is not available on {day.Date:dddd}s."));

                // Clopening: compare with the previous day's shift
                var previous = empDays.FirstOrDefault(d => d.Date == day.Date.AddDays(-1) && d.IsShift);
                if (previous is not null)
                {
                    var rest = ShiftMath.RestHoursBetween(previous.EndTime!.Value, previous.StartTime!.Value, day.StartTime!.Value);
                    if (rest < MinimumRestHours)
                        warnings.Add(new(emp.Id, day.Date, "warning", $"{emp.Name}: only {rest:0.#} h rest before the {day.Date:ddd} shift (clopening)."));
                }
            }

            // Consecutive days (look back into the previous week too)
            var streak = 0;
            DateTime? lastDate = null;
            foreach (var day in empDays)
            {
                streak = day.IsShift && lastDate == day.Date.AddDays(-1) ? streak + 1 : day.IsShift ? 1 : 0;
                lastDate = day.IsShift ? day.Date : null;

                if (streak == 7 && day.Date >= weekStart && day.Date <= weekEnd)
                    warnings.Add(new(emp.Id, day.Date, "error", $"{emp.Name} works 7 days in a row by {day.Date:ddd}. A 24-hour rest is required after 6 consecutive work days."));
            }
        }

        // Station coverage per day (only for days that have any schedule at all)
        foreach (var date in Dates.Range(weekStart, weekEnd))
        {
            var shifts = days.Where(d => d.Date == date && d.IsShift).ToList();
            if (shifts.Count == 0) continue;

            foreach (var station in stations.Where(s => s.MinHeadcount > 0))
            {
                var count = shifts.Count(s => s.StationId == station.Id);
                if (count < station.MinHeadcount)
                    warnings.Add(new(null, date, "warning", $"{date:ddd MMM d}: {station.Name} has {count} of {station.MinHeadcount} required."));
            }
        }

        return warnings;
    }
}
