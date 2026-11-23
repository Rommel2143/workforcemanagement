using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using Dapper;

namespace CakeShopWorkforce.Web.Features.MySchedule;

/// <summary>SLICE: one employee's own shifts (next 14 days) and recent attendance (last 14 days).</summary>
public static class GetMySchedule
{
    public sealed class Result
    {
        public required EmployeeInfo Employee { get; init; }
        public required StoreSetup Setup { get; init; }
        public List<(DateTime Date, AssignmentRow? Plan, bool Published)> Upcoming { get; init; } = [];
        public List<EmployeeDay> Recent { get; init; } = [];
        public decimal UpcomingHours => Upcoming.Sum(u => u.Plan?.PaidHours ?? 0);
    }

    public sealed class Handler(DailyFactsLoader loader, Db db, CurrentStore store, Clock clock)
    {
        public async Task<Result?> HandleAsync(int employeeId)
        {
            var today = clock.Today;
            var data = await loader.LoadAsync(today.AddDays(-14), today.AddDays(13));
            var employee = data.Employee(employeeId);
            if (employee is null) return null;

            await using var conn = await db.OpenAsync();
            var published = (await conn.QueryAsync<DateTime>("""
                select week_start from schedule_weeks
                where store_id = @StoreId and status = 'Published' and week_start between @From::date and @To::date
                """, new { StoreId = store.Id, From = Dates.StartOfWeek(today), To = today.AddDays(13) })).Select(d => d.Date).ToHashSet();

            return new Result
            {
                Employee = employee,
                Setup = data.Setup,
                Upcoming = Dates.Range(today, today.AddDays(13))
                    .Select(d => (d, data.Assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.BusinessDate == d),
                                  published.Contains(Dates.StartOfWeek(d))))
                    .ToList(),
                Recent = data.Days.Where(d => d.Date < today)
                    .SelectMany(d => d.Employees.Where(e => e.Employee.Id == employeeId))
                    .OrderByDescending(e => e.Assignment?.BusinessDate ?? e.Attendance?.BusinessDate)
                    .ToList()
            };
        }
    }
}
