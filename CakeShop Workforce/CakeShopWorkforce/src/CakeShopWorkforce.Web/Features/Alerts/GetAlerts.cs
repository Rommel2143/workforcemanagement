using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;

namespace CakeShopWorkforce.Web.Features.Alerts;

/// <summary>One alert in the bell / alerts page.</summary>
/// <param name="Severity">"danger", "warning" or "info"</param>
/// <param name="Code">The rule code from the plan (A01, A02...)</param>
public sealed record AlertItem(string Severity, string Code, string Category, string Title, string Detail, string? Link);

/// <summary>
/// SLICE: work out today's alerts (spec section 13). Nothing is stored — alerts are
/// recalculated from the facts every time, so they disappear on their own once fixed.
///
///  A01 Insufficient manpower   next 7 days: scheduled people below the forecast ideal
///  A02 Uncovered station       next 7 days: a station below its minimum headcount
///  A03 Absences                today and yesterday
///  A04 Late arrivals           today
///  A05 Excessive overtime      one person above the store's weekly OT alert hours (last 7 days)
///  A06 Schedule conflicts      rule warnings in this week and next week
///  A07 High volume, low staff  yesterday's peak hours with coverage below 0.8
///  A08 Low productivity        TPLH below 90% of target for 3+ days in a row
///  A10 Missing time-out        clocked in, never clocked out
///  A11 Data incomplete         no sales for yesterday
/// </summary>
public static class GetAlerts
{
    public sealed class Handler(DailyFactsLoader loader, Clock clock)
    {
        public async Task<List<AlertItem>> HandleAsync(DateTime date)
        {
            var today = date.Date;
            var alerts = new List<AlertItem>();

            // Load 7 days back (actuals) to 13 days ahead (schedule), plus 6 more days back for 7-day streaks
            var data = await loader.LoadAsync(today.AddDays(-13), today.AddDays(13));
            var setup = data.Setup;

            if (!data.Employees.Any(e => e.IsActive))
            {
                alerts.Add(new("info", "A00", "Setup", "No employees yet",
                    "Add your team on the People page, or load demo data from Settings to try the app.", "employees"));
                return alerts;
            }

            var forecasts = await loader.ForecastAsync(Dates.Range(today, today.AddDays(6)));

            // ---- A01 / A02: the coming 7 days ---------------------------------
            foreach (var day in data.Days.Where(d => d.Date >= today && d.Date <= today.AddDays(6)))
            {
                var link = $"schedule?week={Format.DateIso(Dates.StartOfWeek(day.Date))}";
                var soon = day.Date <= today.AddDays(1);

                if (day.Scheduled == 0)
                {
                    alerts.Add(new(soon ? "danger" : "warning", "A01", "Manpower", $"No schedule for {Format.Date(day.Date)}",
                        "Nobody is scheduled to work yet.", link));
                    continue;
                }

                if (forecasts.TryGetValue(day.Date, out var fc) && fc.HasData)
                {
                    var ideal = ManpowerMath.IdealForTransactions(setup.Stations, fc.Transactions);
                    if (day.Scheduled < ideal)
                        alerts.Add(new(soon ? "danger" : "warning", "A01", "Manpower", $"Insufficient manpower on {Format.Date(day.Date)}",
                            $"{day.Scheduled} scheduled, about {ideal} needed for ~{fc.Transactions:N0} expected transactions.", link));
                }

                var shortStations = setup.Stations
                    .Where(s => s.MinHeadcount > 0 && day.ScheduledByStation.GetValueOrDefault(s.Id) < s.MinHeadcount)
                    .Select(s => $"{s.Name} {day.ScheduledByStation.GetValueOrDefault(s.Id)}/{s.MinHeadcount}")
                    .ToList();
                if (shortStations.Count > 0)
                    alerts.Add(new(soon ? "danger" : "warning", "A02", "Coverage", $"Uncovered station on {Format.Date(day.Date)}",
                        string.Join(", ", shortStations), link));
            }

            // ---- A03 / A04 / A10: attendance ---------------------------------
            foreach (var day in data.Days.Where(d => d.Date == today || d.Date == today.AddDays(-1)))
            {
                var link = $"attendance?date={Format.DateIso(day.Date)}";
                var when = day.Date == today ? "today" : "yesterday";

                var absent = day.Employees.Where(e => e.Result.Status == AttendanceStatus.Absent).Select(e => e.Employee.FullName).ToList();
                if (absent.Count > 0)
                    alerts.Add(new("warning", "A03", "Attendance", $"{absent.Count} absent {when}", string.Join(", ", absent), link));

                if (day.Date == today)
                {
                    var late = day.Employees.Where(e => e.Result.Status == AttendanceStatus.Late)
                        .Select(e => $"{e.Employee.FullName} ({e.Result.LateMinutes} min)").ToList();
                    if (late.Count > 0)
                        alerts.Add(new("info", "A04", "Attendance", $"{late.Count} late {when}", string.Join(", ", late), link));
                }
            }

            foreach (var day in data.Days.Where(d => d.Date >= today.AddDays(-7) && d.Date <= today))
            {
                var missing = day.Employees.Where(e => e.Result.MissingTimeOut && MissingTimeOutIsLate(day.Date, e)).ToList();
                if (missing.Count > 0)
                    alerts.Add(new("warning", "A10", "Attendance", $"Missing time-out on {Format.Date(day.Date)}",
                        string.Join(", ", missing.Select(e => e.Employee.FullName)) + " — hours are not counted until fixed.",
                        $"attendance?date={Format.DateIso(day.Date)}"));
            }

            // ---- A05: overtime in the last 7 days -----------------------------
            var lastWeek = data.Days.Where(d => d.Date > today.AddDays(-7) && d.Date <= today).ToList();
            foreach (var group in lastWeek.SelectMany(d => d.Employees).GroupBy(e => e.Employee.Id))
            {
                var ot = group.Sum(e => e.Result.OvertimeHours);
                if (ot > setup.Store.WeeklyOtAlertHours)
                    alerts.Add(new("warning", "A05", "Overtime", $"Excessive overtime: {group.First().Employee.FullName}",
                        $"{ot:0.#} overtime hours in the last 7 days (alert above {setup.Store.WeeklyOtAlertHours:0.#} h).",
                        $"attendance?date={Format.DateIso(today)}"));
            }

            // ---- A06: schedule rule warnings for this week and next ------------
            var planned = data.Assignments.Select(a => a.ToPlanned()).ToList();
            var people = data.Employees.Select(e => new PlannedEmployee
            {
                Id = e.Id, Name = e.FullName, MaxHoursPerWeek = e.MaxHoursPerWeek,
                AvailableDays = e.AvailableDays, RestDay = e.RestDay, IsActive = e.IsActive
            }).ToList();
            foreach (var weekStart in new[] { Dates.StartOfWeek(today), Dates.StartOfWeek(today).AddDays(7) })
            {
                var warnings = ScheduleRules.Check(weekStart, planned, people, [], setup.Store.RegularHoursPerDay)
                    .Where(w => w.Severity is "error" or "warning").ToList();
                if (warnings.Count == 0) continue;

                var errors = warnings.Count(w => w.Severity == "error");
                alerts.Add(new(errors > 0 ? "danger" : "warning", "A06", "Schedule",
                    $"{warnings.Count} schedule issue{(warnings.Count == 1 ? "" : "s")} in week of {weekStart:MMM d}",
                    warnings[0].Message + (warnings.Count > 1 ? $" (+{warnings.Count - 1} more)" : ""),
                    $"schedule?week={Format.DateIso(weekStart)}"));
            }

            // ---- A07: yesterday's peak hours with too few people ---------------
            var yesterday = data.Day(today.AddDays(-1));
            if (yesterday is { HasHourlySales: true })
            {
                var hours = setup.Store.OperatingHours().Where(h => yesterday.TransactionsByHour[h] > 0).ToList();
                var peakCut = hours.Select(h => yesterday.TransactionsByHour[h]).OrderDescending()
                    .Skip(Math.Max(0, (int)Math.Ceiling(hours.Count * 0.2) - 1)).FirstOrDefault();
                var bad = hours.Where(h => yesterday.TransactionsByHour[h] >= peakCut && peakCut > 0)
                    .Where(h => HourlyDemand.Coverage(yesterday.CounterPresentByHour[h], HourlyDemand.Required(setup, yesterday.TransactionsByHour[h])) < 0.8m)
                    .ToList();
                if (bad.Count > 0)
                    alerts.Add(new("warning", "A07", "Manpower", "High volume, low staff yesterday",
                        "Peak hours with under 80% counter coverage: " + string.Join(", ", bad.Select(Format.HourLabel)) + ".", "analytics"));
            }

            // ---- A08: low productivity 3 days in a row -------------------------
            // Benchmark = the store's own typical (median) TPLH over the loaded past days,
            // so the alert means "worse than usual for us", not "worse than a generic number".
            var pastDays = data.Days.Where(d => d.Date < today).ToList();
            var typical = PeriodSummary.Median(pastDays.Where(d => d.HasSales).Select(d => d.Manpower.TransactionsPerLaborHour));
            if (typical is not null && pastDays.Count(d => d.HasSales) >= 7)
            {
                var floor = Math.Round(typical.Value * 0.85m, 1);
                var streak = 0;
                foreach (var day in lastWeek.Where(d => d.Date < today))
                {
                    streak = day.Manpower.TransactionsPerLaborHour is { } tplh && tplh < floor ? streak + 1 : 0;
                    if (streak == 3)
                    {
                        alerts.Add(new("info", "A08", "Productivity", "Low productivity 3 days in a row",
                            $"Transactions per labor hour stayed below {floor:0.#} (85% of the typical {typical:0.#}) through {Format.Date(day.Date)}. Check whether hours match traffic.",
                            "analytics"));
                        break;
                    }
                }
            }

            // ---- A11: sales not entered for yesterday --------------------------
            if (yesterday is { HasSales: false } && yesterday.Present > 0 && clock.Now.Hour >= 10)
                alerts.Add(new("warning", "A11", "Data", $"No sales entered for {Format.Date(yesterday.Date)}",
                    "Productivity numbers for that day show \"—\" until sales are entered or imported.", "sales"));

            return alerts
                .OrderBy(a => a.Severity switch { "danger" => 0, "warning" => 1, _ => 2 })
                .ToList();
        }

        /// <summary>Only complain about a missing time-out once the shift is over (+1 hour).</summary>
        private bool MissingTimeOutIsLate(DateTime date, EmployeeDay e)
        {
            if (date < clock.Today) return true;
            var end = e.Assignment?.EndTime;
            return end is not null && clock.Now.TimeOfDay > end.Value + TimeSpan.FromHours(1);
        }
    }
}
