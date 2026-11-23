using CakeShopWorkforce.Web.Features.Alerts;
using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;

namespace CakeShopWorkforce.Web.Features.Dashboard;

/// <summary>SLICE: everything the dashboard shows for the chosen period.</summary>
public static class GetDashboard
{
    public sealed class Result
    {
        public required Period Period { get; init; }
        public required StoreSetup Setup { get; init; }
        public required PeriodSummary Summary { get; init; }

        /// <summary>Days used by the charts (at least one full week, so a single day still has context).</summary>
        public List<DayFacts> ChartDays { get; init; } = [];

        public DayFacts? Today { get; init; }
        public List<AlertItem> Alerts { get; init; } = [];

        /// <summary>For a single day: average of the same weekday over the 4 weeks before (spec 14.1 "today vs same weekday").</summary>
        public Forecast? SameWeekday { get; init; }

        /// <summary>Average transactions per clock hour across the chart days.</summary>
        public List<(int Hour, double Transactions, double Present)> PeakHours { get; init; } = [];
    }

    public sealed class Handler(DailyFactsLoader loader, GetAlerts.Handler alerts, Clock clock)
    {
        public async Task<Result> HandleAsync(Period period)
        {
            var today = clock.Today;
            var chartFrom = period.Days >= 7 ? period.From : Dates.StartOfWeek(period.From);
            var chartTo = period.Days >= 7 ? period.To : Dates.StartOfWeek(period.From).AddDays(6);

            var from = new[] { chartFrom, period.From, today }.Min();
            var to = new[] { chartTo, period.To, today }.Max();
            var data = await loader.LoadAsync(from, to);

            var periodDays = data.Days.Where(d => d.Date >= period.From && d.Date <= period.To).ToList();
            var chartDays = data.Days.Where(d => d.Date >= chartFrom && d.Date <= chartTo).ToList();

            Forecast? sameWeekday = null;
            if (period.Days == 1)
                sameWeekday = (await loader.ForecastAsync([period.From]))[period.From];

            var hourlyDays = chartDays.Where(d => d.HasHourlySales).ToList();
            var peak = data.Setup.Store.OperatingHours()
                .Select(h => (h,
                    hourlyDays.Count == 0 ? 0 : hourlyDays.Average(d => d.TransactionsByHour[h]),
                    hourlyDays.Count == 0 ? 0 : hourlyDays.Average(d => d.CounterPresentByHour[h])))
                .ToList();

            return new Result
            {
                Period = period,
                Setup = data.Setup,
                Summary = new PeriodSummary(periodDays),
                ChartDays = chartDays,
                Today = data.Day(today),
                Alerts = (await alerts.HandleAsync(today)).Take(6).ToList(),
                SameWeekday = sameWeekday,
                PeakHours = hourlyDays.Count == 0 ? [] : peak
            };
        }
    }
}
