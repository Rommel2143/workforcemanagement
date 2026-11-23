using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;

namespace CakeShopWorkforce.Web.Features.Analytics;

/// <summary>
/// SLICE: the full manpower analysis for a period — daily table, hourly / peak-hour view,
/// shift suggestions and plain-language answers to the manager's questions.
/// </summary>
public static class GetManpowerAnalysis
{
    public sealed class HourRow
    {
        public int Hour { get; init; }
        public double AvgTransactions { get; init; }
        public double AvgPresent { get; init; }
        public int Required { get; init; }
        public decimal? Coverage { get; init; }
        public double? TransactionsPerPerson => AvgPresent <= 0 ? null : Math.Round(AvgTransactions / AvgPresent, 1);
        public string Tag { get; set; } = "";     // "Peak", "Low" or ""
    }

    public sealed class WeekdayRow
    {
        public int DayIndex { get; init; }        // 0 = Monday
        public string Day => Dates.DayCodes[DayIndex];
        public int Samples { get; init; }
        public double AvgTransactions { get; init; }
        public double AvgPresent { get; init; }
        public double AvgIdeal { get; init; }
        public List<(ShiftTemplate Template, int Count)> SuggestedShifts { get; init; } = [];
        public int SuggestedPeople => SuggestedShifts.Sum(s => s.Count);
    }

    public sealed class StationRow
    {
        public required Station Station { get; init; }
        public int DaysShort { get; init; }
        public double AvgPresent { get; init; }
        public double AvgRequired { get; init; }
    }

    public sealed record Insight(string Question, string Answer, string Tone);

    public sealed class Result
    {
        public required Period Period { get; init; }
        public required StoreSetup Setup { get; init; }
        public required PeriodSummary Summary { get; init; }
        public List<DayFacts> Days => Summary.Days;

        public DateTime HourlyFrom { get; init; }
        public DateTime HourlyTo { get; init; }
        public int HourlyDays { get; init; }
        public List<HourRow> Hours { get; init; } = [];
        public double[,] Heatmap { get; init; } = new double[7, 0];
        public List<int> HeatmapHours { get; init; } = [];
        public List<WeekdayRow> Weekdays { get; init; } = [];
        public List<StationRow> Stations { get; init; } = [];
        public List<Insight> Insights { get; init; } = [];
        public List<string> Recommendations { get; init; } = [];
    }

    public sealed class Handler(DailyFactsLoader loader)
    {
        public async Task<Result> HandleAsync(Period period)
        {
            // Hour patterns need several weeks of data. For short periods we look at the 4 weeks ending on the last day.
            var hourlyFrom = period.Days >= 14 ? period.From : period.To.AddDays(-27);
            var data = await loader.LoadAsync(hourlyFrom, period.To);
            var setup = data.Setup;

            var periodDays = data.Days.Where(d => d.Date >= period.From).ToList();
            var summary = new PeriodSummary(periodDays);
            var hourDays = data.Days.Where(d => d.HasHourlySales).ToList();
            var operating = setup.Store.OperatingHours().ToList();

            // ---- Hourly table -------------------------------------------------
            var hours = operating.Select(h =>
            {
                var tx = hourDays.Count == 0 ? 0 : hourDays.Average(d => d.TransactionsByHour[h]);
                var present = hourDays.Count == 0 ? 0 : hourDays.Average(d => d.CounterPresentByHour[h]);
                var required = HourlyDemand.Required(setup, tx);
                return new HourRow
                {
                    Hour = h,
                    AvgTransactions = Math.Round(tx, 1),
                    AvgPresent = Math.Round(present, 1),
                    Required = required,
                    Coverage = required == 0 ? null : Math.Round((decimal)(present / required), 2)
                };
            }).ToList();

            // Peak = top 20% of hours by transactions, Low = bottom 20% (spec 12.2)
            if (hourDays.Count > 0 && hours.Count >= 5)
            {
                var ranked = hours.OrderByDescending(h => h.AvgTransactions).ToList();
                var n = Math.Max(1, (int)Math.Round(hours.Count * 0.2));
                foreach (var h in ranked.Take(n)) h.Tag = "Peak";
                foreach (var h in ranked.TakeLast(n)) h.Tag = "Low";
            }

            // ---- Heatmap: weekday × hour, average transactions ----------------
            var heat = new double[7, operating.Count];
            for (var wd = 0; wd < 7; wd++)
            {
                var sameDay = hourDays.Where(d => DayIndex(d.Date) == wd).ToList();
                if (sameDay.Count == 0) continue;
                for (var i = 0; i < operating.Count; i++)
                    heat[wd, i] = Math.Round(sameDay.Average(d => d.TransactionsByHour[operating[i]]), 1);
            }

            // ---- Per weekday: average traffic and a suggested shift mix --------
            var weekdays = new List<WeekdayRow>();
            for (var wd = 0; wd < 7; wd++)
            {
                var sameDay = data.Days.Where(d => d.HasSales && DayIndex(d.Date) == wd).ToList();
                var sameDayHourly = sameDay.Where(d => d.HasHourlySales).ToList();
                var required = Enumerable.Range(0, 24)
                    .Select(h => sameDayHourly.Count == 0 ? 0 : HourlyDemand.Required(setup, sameDayHourly.Average(d => d.TransactionsByHour[h])))
                    .ToArray();
                weekdays.Add(new WeekdayRow
                {
                    DayIndex = wd,
                    Samples = sameDay.Count,
                    AvgTransactions = sameDay.Count == 0 ? 0 : Math.Round(sameDay.Average(d => d.Transactions), 0),
                    AvgPresent = sameDay.Count == 0 ? 0 : Math.Round(sameDay.Average(d => d.Present), 1),
                    AvgIdeal = sameDay.Count == 0 ? 0 : Math.Round(sameDay.Average(d => d.Manpower.Ideal), 1),
                    SuggestedShifts = sameDayHourly.Count == 0 ? [] : HourlyDemand.SuggestShifts(setup, required)
                });
            }

            // ---- Per station: how often were we short? ------------------------
            var salesDays = periodDays.Where(d => d.HasSales).ToList();
            var stations = setup.Stations.Select(s =>
            {
                var rows = salesDays.Select(d => d.Manpower.Stations.First(x => x.Station.Id == s.Id)).ToList();
                return new StationRow
                {
                    Station = s,
                    DaysShort = rows.Count(r => r.Gap < 0),
                    AvgPresent = rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Present), 1),
                    AvgRequired = rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Required), 1)
                };
            }).ToList();

            var result = new Result
            {
                Period = period,
                Setup = setup,
                Summary = summary,
                HourlyFrom = hourlyFrom,
                HourlyTo = period.To,
                HourlyDays = hourDays.Count,
                Hours = hours,
                Heatmap = heat,
                HeatmapHours = operating,
                Weekdays = weekdays,
                Stations = stations
            };
            result.Insights.AddRange(BuildInsights(result));
            result.Recommendations.AddRange(BuildRecommendations(result));
            return result;
        }

        private static int DayIndex(DateTime date) => ((int)date.DayOfWeek + 6) % 7;

        /// <summary>The manager's eight questions, answered in plain words.</summary>
        private static IEnumerable<Insight> BuildInsights(Result r)
        {
            var s = r.Summary;
            var setup = r.Setup;

            if (s.DaysWithSales == 0)
            {
                yield return new("Do we have enough data?", "No sales are recorded in this period yet. Enter or import sales on the Sales page to unlock the analysis.", "neutral");
                yield break;
            }

            // 1. Properly staffed?
            yield return new("1. Are we properly staffed?",
                $"{s.OptimalDays} optimal, {s.UnderstaffedDays} understaffed and {s.OverstaffedDays} potentially overstaffed day(s) out of {s.DaysWithSales} with sales.",
                s.UnderstaffedDays > s.OverstaffedDays ? "danger" : s.OverstaffedDays > 0 ? "warning" : "success");

            // 2. Busiest days
            var busiest = r.Weekdays.Where(w => w.Samples > 0).OrderByDescending(w => w.AvgTransactions).ToList();
            if (busiest.Count > 0)
                yield return new("2. Which days are busiest?",
                    $"{busiest[0].Day} averages {busiest[0].AvgTransactions:N0} transactions" +
                    (busiest.Count > 1 ? $"; the quietest is {busiest[^1].Day} with {busiest[^1].AvgTransactions:N0}." : "."),
                    "info");

            // 3. Peak hours
            var peaks = r.Hours.Where(h => h.Tag == "Peak").OrderBy(h => h.Hour).ToList();
            yield return peaks.Count > 0
                ? new("3. What are our peak hours?",
                    string.Join(", ", peaks.Select(h => Format.HourLabel(h.Hour))) + $" (top 20% of hourly transactions, based on {r.HourlyDays} day(s) of hourly data).",
                    "info")
                : new("3. What are our peak hours?", "Enter hourly sales (or import a CSV with an hour column) to find peak hours.", "neutral");

            // 4. How many people per day
            var avgIdeal = r.Days.Where(d => d.HasSales).Average(d => d.Manpower.Ideal);
            var avgPresent = r.Days.Where(d => d.HasSales).Average(d => d.Present);
            yield return new("4. How many people do we need per day?",
                $"About {avgIdeal:0.#} on average (station-aware ideal); we had {avgPresent:0.#} present on average.",
                avgPresent < avgIdeal ? "danger" : "success");

            // 5. Which station is short
            var worst = r.Stations.OrderByDescending(x => x.DaysShort).FirstOrDefault();
            yield return worst is { DaysShort: > 0 }
                ? new("5. Which station is short most often?", $"{worst.Station.Name}: short on {worst.DaysShort} day(s) (avg {worst.AvgPresent:0.#} present vs {worst.AvgRequired:0.#} required).", "warning")
                : new("5. Which station is short most often?", "No station was below its requirement on days with sales.", "success");

            // 6. Attendance
            yield return new("6. Is attendance reliable?",
                $"Attendance rate {Format.Percent(s.AttendanceRate)} with {s.Absent} absence(s) and {s.Late} late arrival(s).",
                s.AttendanceRate is null ? "neutral" : s.AttendanceRate >= 95 ? "success" : s.AttendanceRate >= 85 ? "warning" : "danger");

            // 7. Productivity — compared with the store's own days (TPLH counts every labor hour, bakers included)
            var ranked = r.Days.Where(d => d.Manpower.TransactionsPerLaborHour is not null)
                .OrderByDescending(d => d.Manpower.TransactionsPerLaborHour).ToList();
            yield return new("7. How productive is each labor hour?",
                $"{Format.OrDash(s.TransactionsPerLaborHour)} transactions and {(s.SalesPerLaborHour is null ? "—" : Format.Money(s.SalesPerLaborHour.Value))} sales per labor hour (all staff)." +
                (ranked.Count > 1
                    ? $" Best day {Format.Date(ranked[0].Date)} ({ranked[0].Manpower.TransactionsPerLaborHour:0.#}), weakest {Format.Date(ranked[^1].Date)} ({ranked[^1].Manpower.TransactionsPerLaborHour:0.#})."
                    : ""),
                "info");

            // 8. Schedule changes
            var shortHours = r.Hours.Where(h => h.Coverage is < 0.9m && h.AvgTransactions > 0).ToList();
            var slackHours = r.Hours.Where(h => h.Coverage is > 1.4m).ToList();
            yield return new("8. What schedule changes would help?",
                shortHours.Count > 0
                    ? $"Add cover around {string.Join(", ", shortHours.Take(3).Select(h => Format.HourLabel(h.Hour)))}" +
                      (slackHours.Count > 0 ? $", by moving hours from quieter times such as {Format.HourLabel(slackHours[0].Hour)}." : ".")
                    : slackHours.Count > 0
                        ? $"Coverage is fine. Quieter hours ({string.Join(", ", slackHours.Take(3).Select(h => Format.HourLabel(h.Hour)))}) could absorb breaks or prep work."
                        : "Hourly coverage matches demand. Keep the current shift pattern.",
                shortHours.Count > 0 ? "warning" : "success");
        }

        /// <summary>Recommendations move hours around. They never say "cut people" (spec 9.5 safety rule).</summary>
        private static IEnumerable<string> BuildRecommendations(Result r)
        {
            foreach (var station in r.Stations.Where(s => s.DaysShort > 0).OrderByDescending(s => s.DaysShort))
                yield return $"{station.Station.Name} was short on {station.DaysShort} day(s). Schedule at least {Math.Ceiling(station.AvgRequired)} there, or cross-train someone from a quieter station.";

            var peakShort = r.Hours.Where(h => h.Tag == "Peak" && h.Coverage is < 1m).OrderBy(h => h.Hour).ToList();
            if (peakShort.Count > 0)
            {
                var first = peakShort[0].Hour;
                var mid = r.Setup.ShiftTemplates.Where(t => t.IsActive)
                    .OrderBy(t => Math.Abs(t.StartTime.TotalHours - (first - 1))).FirstOrDefault();
                yield return $"Peak hours {string.Join(", ", peakShort.Select(h => Format.HourLabel(h.Hour)))} are under-covered. " +
                             (mid is null ? "Add a shift that starts about an hour before." : $"Add a {mid.Name} ({Format.TimeRange(mid.StartTime, mid.EndTime)}) on busy days.");
            }

            var lowSlack = r.Hours.Where(h => h.Tag == "Low" && h.Coverage is > 1.3m).ToList();
            if (lowSlack.Count > 0)
                yield return $"Quiet hours ({string.Join(", ", lowSlack.Select(h => Format.HourLabel(h.Hour)))}) have extra people. Before reducing anyone, check station coverage — consider starting one shift later or using that time for production prep.";

            var busiest = r.Weekdays.Where(w => w.Samples > 0 && w.AvgPresent < w.AvgIdeal).OrderByDescending(w => w.AvgIdeal - w.AvgPresent).FirstOrDefault();
            if (busiest is not null)
                yield return $"{busiest.Day}s average {busiest.AvgPresent:0.#} people but need about {busiest.AvgIdeal:0.#}. Move a rest day away from {busiest.Day}.";

            if (r.Summary.OvertimeHours > 0)
                yield return $"{r.Summary.OvertimeHours:0.#} overtime hours in this period. Spreading shifts more evenly can lower overtime.";

            if (r.Summary.MissingTimeOut > 0)
                yield return $"{r.Summary.MissingTimeOut} attendance record(s) have no time-out; their hours are not counted. Fix them on the Attendance page.";
        }
    }
}
