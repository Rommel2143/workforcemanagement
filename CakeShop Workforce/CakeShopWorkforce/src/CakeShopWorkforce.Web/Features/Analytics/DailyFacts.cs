using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Analytics;

// =============================================================================
//  DAILY FACTS — the "calculation engine" every screen shares.
//
//  Design rule from the plan: "every module writes facts; only the engines compute".
//  Schedule, Attendance and Sales pages only SAVE raw data. This file reads
//  that raw data for a date range and turns it into one DayFacts per date:
//
//      schedule rows  ─┐
//      attendance rows ├──►  DailyFactsLoader.LoadAsync(from, to)  ──►  FactsData.Days
//      sales rows     ─┘                                                (one DayFacts per date)
//
//  Dashboard, Analytics, Alerts, Reports and My Shifts all use this, so the
//  numbers are always the same on every screen.
// =============================================================================

// ---- Raw rows straight from the database ------------------------------------

public sealed class EmployeeInfo
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Position { get; set; } = "";
    public int? StationId { get; set; }
    public string EmploymentType { get; set; } = "";
    public string AvailableDays { get; set; } = "";
    public int MaxHoursPerWeek { get; set; }
    public string? RestDay { get; set; }
    public bool IsActive { get; set; }
}

public sealed class AssignmentRow
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime BusinessDate { get; set; }
    public string AssignmentType { get; set; } = "Shift";
    public int? ShiftTemplateId { get; set; }
    public int? StationId { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public int BreakMinutes { get; set; }
    public string? LeaveType { get; set; }
    public string? Note { get; set; }

    public bool IsShift => AssignmentType == "Shift" && StartTime is not null && EndTime is not null;
    public decimal PaidHours => IsShift ? ShiftMath.PaidHours(StartTime!.Value, EndTime!.Value, BreakMinutes) : 0;

    /// <summary>The shape <see cref="ScheduleRules"/> needs.</summary>
    public PlannedDay ToPlanned() => new()
    {
        EmployeeId = EmployeeId,
        Date = BusinessDate,
        AssignmentType = AssignmentType,
        StartTime = StartTime,
        EndTime = EndTime,
        BreakMinutes = BreakMinutes,
        StationId = StationId
    };
}

public sealed class AttendanceRow
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime BusinessDate { get; set; }
    public TimeSpan? TimeIn { get; set; }
    public TimeSpan? TimeOut { get; set; }
    public int BreakMinutes { get; set; }
    public bool IsAbsent { get; set; }
    public string? Note { get; set; }
}

public sealed class SalesDayRow
{
    public DateTime BusinessDate { get; set; }
    public int Transactions { get; set; }
    public decimal Sales { get; set; }
}

public sealed class SalesHourRow
{
    public DateTime BusinessDate { get; set; }
    public int Hour { get; set; }
    public int Transactions { get; set; }
    public decimal Sales { get; set; }
}

// ---- Computed facts ---------------------------------------------------------

/// <summary>One employee on one day: what was planned, what happened, and the verdict.</summary>
public sealed class EmployeeDay
{
    public required EmployeeInfo Employee { get; init; }
    public AssignmentRow? Assignment { get; init; }
    public AttendanceRow? Attendance { get; init; }
    public required AttendanceResult Result { get; init; }

    /// <summary>Station for the day: the assignment's station, else the employee's home station.</summary>
    public int? StationId => Assignment?.StationId ?? Employee.StationId;
}

/// <summary>Everything we know about one business date.</summary>
public sealed class DayFacts
{
    public DateTime Date { get; init; }
    public List<EmployeeDay> Employees { get; init; } = [];

    // Headcounts (spec 9.1)
    public int Scheduled { get; init; }          // Shift assignments only (no rest day, no leave)
    public int Present { get; init; }
    public int Absent { get; init; }
    public int Late { get; init; }
    public int OnLeave { get; init; }
    public int RestDay { get; init; }
    public int Pending { get; init; }            // shift not started yet
    public int MissingTimeOut { get; init; }

    public decimal ScheduledHours { get; init; }
    public decimal LaborHours { get; init; }     // actual worked hours (missing time-outs excluded)
    public decimal OvertimeHours { get; init; }

    // Sales
    public bool HasSales { get; init; }
    public int Transactions { get; init; }
    public decimal Sales { get; init; }
    public bool HasHourlySales { get; init; }
    public int[] TransactionsByHour { get; init; } = new int[24];
    public decimal[] SalesByHour { get; init; } = new decimal[24];

    // People on duty per clock hour (actual and planned)
    public int[] PresentByHour { get; init; } = new int[24];
    public int[] ScheduledByHour { get; init; } = new int[24];

    /// <summary>
    /// Counter staff on duty per clock hour (people at transaction-driven stations: Cashier, Sales).
    /// Hourly coverage compares THIS with customer traffic — bakers and decorators work to
    /// production, not to walk-ins, so they are not counted against hourly demand.
    /// </summary>
    public int[] CounterPresentByHour { get; init; } = new int[24];

    public Dictionary<int, int> PresentByStation { get; init; } = [];
    public Dictionary<int, int> ScheduledByStation { get; init; } = [];

    /// <summary>All the productivity numbers (ATV, TPP, SPP, TPLH, ideal, gap, status).</summary>
    public required ManpowerResult Manpower { get; init; }

    /// <summary>Present ÷ (Scheduled − people whose shift has not started). Rest days and leave never count.</summary>
    public decimal? AttendanceRate
    {
        get
        {
            var expected = Scheduled - Pending;
            if (expected <= 0) return null;
            var cameIn = Employees.Count(e => e.Assignment?.AssignmentType == "Shift" && e.Result.IsPresent);
            return Math.Round(cameIn * 100m / expected, 0);
        }
    }
}

/// <summary>Average of the same weekday over the last 4 weeks — the simple, explainable forecast (spec 12.1).</summary>
public sealed class Forecast
{
    public DateTime Date { get; init; }
    public int SampleDays { get; init; }          // how many past weeks had data (0 = no forecast)
    public decimal Transactions { get; init; }
    public decimal Sales { get; init; }
    public double[] TransactionsByHour { get; init; } = new double[24];

    public bool HasData => SampleDays > 0;
}

/// <summary>Everything loaded for a date range.</summary>
public sealed class FactsData
{
    public required StoreSetup Setup { get; init; }
    public List<EmployeeInfo> Employees { get; init; } = [];
    public List<AssignmentRow> Assignments { get; init; } = [];
    public List<AttendanceRow> Attendance { get; init; } = [];
    public List<DayFacts> Days { get; init; } = [];

    public DayFacts? Day(DateTime date) => Days.FirstOrDefault(d => d.Date == date.Date);

    public EmployeeInfo? Employee(int id) => Employees.FirstOrDefault(e => e.Id == id);
}

/// <summary>Totals and averages over several days (the KPI cards for a week or a month).</summary>
public sealed class PeriodSummary
{
    public List<DayFacts> Days { get; }

    public PeriodSummary(IEnumerable<DayFacts> days)
    {
        Days = days.OrderBy(d => d.Date).ToList();
        var withSales = Days.Where(d => d.HasSales).ToList();

        DaysWithSales = withSales.Count;
        Transactions = Days.Sum(d => d.Transactions);
        Sales = Days.Sum(d => d.Sales);
        Scheduled = Days.Sum(d => d.Scheduled);
        Present = Days.Sum(d => d.Present);
        Absent = Days.Sum(d => d.Absent);
        Late = Days.Sum(d => d.Late);
        OnLeave = Days.Sum(d => d.OnLeave);
        LaborHours = Days.Sum(d => d.LaborHours);
        OvertimeHours = Days.Sum(d => d.OvertimeHours);
        MissingTimeOut = Days.Sum(d => d.MissingTimeOut);

        // Productivity is only fair on days where we know the sales
        var presentOnSalesDays = withSales.Sum(d => d.Present);
        var hoursOnSalesDays = withSales.Sum(d => d.LaborHours);
        Ideal = withSales.Sum(d => d.Manpower.Ideal);
        PresentOnSalesDays = presentOnSalesDays;

        AverageTransactionValue = ManpowerMath.Divide(Sales, Transactions);
        TransactionsPerPersonnel = ManpowerMath.Divide(Transactions, presentOnSalesDays, 1);
        SalesPerPersonnel = ManpowerMath.Divide(Sales, presentOnSalesDays);
        TransactionsPerLaborHour = ManpowerMath.Divide(Transactions, hoursOnSalesDays, 1);
        SalesPerLaborHour = ManpowerMath.Divide(Sales, hoursOnSalesDays);
        Utilization = presentOnSalesDays == 0 ? null : Math.Round(Ideal * 100m / presentOnSalesDays, 0);

        var expected = Days.Sum(d => d.Scheduled - d.Pending);
        var cameIn = Days.Sum(d => d.Employees.Count(e => e.Assignment?.AssignmentType == "Shift" && e.Result.IsPresent));
        AttendanceRate = expected <= 0 ? null : Math.Round(cameIn * 100m / expected, 0);

        MedianTransactionsPerLaborHour = Median(withSales.Select(d => d.Manpower.TransactionsPerLaborHour));

        UnderstaffedDays = withSales.Count(d => d.Manpower.Status == StaffingStatus.Understaffed);
        OptimalDays = withSales.Count(d => d.Manpower.Status == StaffingStatus.Optimal);
        OverstaffedDays = withSales.Count(d => d.Manpower.Status == StaffingStatus.PotentiallyOverstaffed);

        Status = Days.Count == 1
            ? Days[0].Manpower.Status
            : DaysWithSales == 0 ? StaffingStatus.NoData
            : Gap < 0 || UnderstaffedDays * 2 > DaysWithSales ? StaffingStatus.Understaffed
            : OverstaffedDays * 2 > DaysWithSales ? StaffingStatus.PotentiallyOverstaffed
            : StaffingStatus.Optimal;
    }

    public int DaysWithSales { get; }
    public int Transactions { get; }
    public decimal Sales { get; }
    public int Scheduled { get; }               // person-days
    public int Present { get; }                 // person-days
    public int PresentOnSalesDays { get; }
    public int Absent { get; }
    public int Late { get; }
    public int OnLeave { get; }
    public int MissingTimeOut { get; }
    public decimal LaborHours { get; }
    public decimal OvertimeHours { get; }

    public decimal? AverageTransactionValue { get; }
    public decimal? TransactionsPerPersonnel { get; }
    public decimal? SalesPerPersonnel { get; }
    public decimal? TransactionsPerLaborHour { get; }
    public decimal? SalesPerLaborHour { get; }
    public decimal? AttendanceRate { get; }

    /// <summary>
    /// The store's own typical TPLH: the middle value of the days in the period.
    /// Used as the productivity benchmark (spec 12.3 "suggested benchmark") because
    /// TPLH counts ALL labor hours — bakers too — so a counter-only target would not fit.
    /// </summary>
    public decimal? MedianTransactionsPerLaborHour { get; }

    public static decimal? Median(IEnumerable<decimal?> values)
    {
        var sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
        if (sorted.Count == 0) return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : Math.Round((sorted[mid - 1] + sorted[mid]) / 2, 1);
    }

    public int Ideal { get; }                   // person-days needed (days with sales)
    public int Gap => PresentOnSalesDays - Ideal;
    public decimal? Utilization { get; }

    public int UnderstaffedDays { get; }
    public int OptimalDays { get; }
    public int OverstaffedDays { get; }
    public StaffingStatus Status { get; }
}

/// <summary>
/// Loads schedule + attendance + sales for a date range and computes <see cref="DayFacts"/>.
/// Registered as a scoped service; inject it into any handler.
/// </summary>
public sealed class DailyFactsLoader(Db db, CurrentStore store, StoreConfig config, Clock clock)
{
    public async Task<FactsData> LoadAsync(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        var setup = await config.LoadAsync();

        await using var conn = await db.OpenAsync();
        var p = new { StoreId = store.Id, From = from, To = to };

        var employees = (await conn.QueryAsync<EmployeeInfo>(
            "select * from employees where store_id = @StoreId order by full_name", p)).ToList();

        var assignments = (await conn.QueryAsync<AssignmentRow>("""
            select * from schedule_assignments
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToList();

        var attendance = (await conn.QueryAsync<AttendanceRow>("""
            select * from attendance
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToList();

        var salesDays = (await conn.QueryAsync<SalesDayRow>("""
            select business_date, transactions, sales from sales_daily
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToDictionary(s => s.BusinessDate.Date);

        var salesHours = (await conn.QueryAsync<SalesHourRow>("""
            select business_date, hour, transactions, sales from sales_hourly
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToLookup(s => s.BusinessDate.Date);

        var employeeById = employees.ToDictionary(e => e.Id);
        var assignmentsByDate = assignments.ToLookup(a => a.BusinessDate.Date);
        var attendanceByDate = attendance.ToLookup(a => a.BusinessDate.Date);

        var days = Dates.Range(from, to)
            .Select(date => BuildDay(date, setup, employeeById, assignmentsByDate[date].ToList(),
                attendanceByDate[date].ToList(), salesDays.GetValueOrDefault(date), salesHours[date].ToList()))
            .ToList();

        return new FactsData
        {
            Setup = setup,
            Employees = employees,
            Assignments = assignments,
            Attendance = attendance,
            Days = days
        };
    }

    private DayFacts BuildDay(
        DateTime date,
        StoreSetup setup,
        Dictionary<int, EmployeeInfo> employeeById,
        List<AssignmentRow> assignments,
        List<AttendanceRow> attendance,
        SalesDayRow? sales,
        List<SalesHourRow> hourly)
    {
        var now = clock.Now;
        var settings = setup.Store;

        // Everyone who has a plan OR a time record on this date
        var employeeIds = assignments.Select(a => a.EmployeeId).Union(attendance.Select(a => a.EmployeeId)).Distinct();

        var rows = new List<EmployeeDay>();
        foreach (var id in employeeIds)
        {
            if (!employeeById.TryGetValue(id, out var emp)) continue;
            var plan = assignments.FirstOrDefault(a => a.EmployeeId == id);
            var actual = attendance.FirstOrDefault(a => a.EmployeeId == id);

            var result = AttendanceMath.Evaluate(
                plan?.AssignmentType,
                plan?.StartTime,
                actual?.TimeIn,
                actual?.TimeOut,
                actual?.BreakMinutes ?? plan?.BreakMinutes ?? 60,
                actual?.IsAbsent ?? false,
                date, now, settings.GraceMinutes, settings.RegularHoursPerDay);

            rows.Add(new EmployeeDay { Employee = emp, Assignment = plan, Attendance = actual, Result = result });
        }
        rows = rows.OrderBy(r => r.Assignment?.StartTime ?? TimeSpan.MaxValue).ThenBy(r => r.Employee.FullName).ToList();

        // People on duty per clock hour
        var presentByHour = new int[24];
        var scheduledByHour = new int[24];
        var counterByHour = new int[24];
        foreach (var r in rows)
        {
            var atCounter = setup.FindStation(r.StationId)?.IsTransactionDriven == true;

            if (r.Assignment is { IsShift: true } a)
                for (var h = 0; h < 24; h++)
                    if (ShiftMath.CoversHour(a.StartTime!.Value, a.EndTime!.Value, h)) scheduledByHour[h]++;

            if (r.Result.IsPresent && r.Attendance?.TimeIn is { } timeIn)
            {
                // No time-out yet: assume they stay until the scheduled end (or closing time)
                var timeOut = r.Attendance.TimeOut
                              ?? (date == clock.Today ? now.TimeOfDay : r.Assignment?.EndTime ?? settings.CloseTime);
                for (var h = 0; h < 24; h++)
                {
                    if (!ShiftMath.CoversHour(timeIn, timeOut, h)) continue;
                    presentByHour[h]++;
                    if (atCounter) counterByHour[h]++;
                }
            }
        }

        var present = rows.Where(r => r.Result.IsPresent).ToList();
        var presentByStation = present.Where(r => r.StationId is not null)
            .GroupBy(r => r.StationId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var scheduledByStation = rows.Where(r => r.Assignment is { IsShift: true } && r.StationId is not null)
            .GroupBy(r => r.StationId!.Value).ToDictionary(g => g.Key, g => g.Count());

        var txByHour = new int[24];
        var salesByHour = new decimal[24];
        foreach (var h in hourly)
        {
            txByHour[h.Hour] = h.Transactions;
            salesByHour[h.Hour] = h.Sales;
        }

        var laborHours = rows.Sum(r => r.Result.WorkedHours);
        var transactions = sales?.Transactions ?? 0;
        var salesAmount = sales?.Sales ?? 0;

        var manpower = ManpowerMath.Analyze(
            transactions, salesAmount, sales is not null,
            present.Count, laborHours, presentByStation, setup.Stations, settings.TargetTxPerPersonnel);

        return new DayFacts
        {
            Date = date,
            Employees = rows,
            Scheduled = rows.Count(r => r.Assignment?.AssignmentType == "Shift"),
            Present = present.Count,
            Absent = rows.Count(r => r.Result.Status == AttendanceStatus.Absent),
            Late = rows.Count(r => r.Result.Status == AttendanceStatus.Late),
            OnLeave = rows.Count(r => r.Result.Status == AttendanceStatus.OnLeave),
            RestDay = rows.Count(r => r.Result.Status == AttendanceStatus.RestDay),
            Pending = rows.Count(r => r.Result.Status == AttendanceStatus.Pending),
            MissingTimeOut = rows.Count(r => r.Result.MissingTimeOut),
            ScheduledHours = rows.Sum(r => r.Assignment?.PaidHours ?? 0),
            LaborHours = laborHours,
            OvertimeHours = rows.Sum(r => r.Result.OvertimeHours),
            HasSales = sales is not null,
            Transactions = transactions,
            Sales = salesAmount,
            HasHourlySales = hourly.Count > 0,
            TransactionsByHour = txByHour,
            SalesByHour = salesByHour,
            PresentByHour = presentByHour,
            ScheduledByHour = scheduledByHour,
            CounterPresentByHour = counterByHour,
            PresentByStation = presentByStation,
            ScheduledByStation = scheduledByStation,
            Manpower = manpower
        };
    }

    /// <summary>
    /// Forecast for each date = average of the same weekday in the 4 weeks before it
    /// (only weeks that have sales). Simple on purpose: a manager can check it by hand.
    /// </summary>
    public async Task<Dictionary<DateTime, Forecast>> ForecastAsync(IEnumerable<DateTime> dates)
    {
        var list = dates.Select(d => d.Date).Distinct().ToList();
        if (list.Count == 0) return [];

        var from = list.Min().AddDays(-28);
        var to = list.Max().AddDays(-7);

        await using var conn = await db.OpenAsync();
        var p = new { StoreId = store.Id, From = from, To = to };

        var daily = (await conn.QueryAsync<SalesDayRow>("""
            select business_date, transactions, sales from sales_daily
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToDictionary(s => s.BusinessDate.Date);

        var hourly = (await conn.QueryAsync<SalesHourRow>("""
            select business_date, hour, transactions, sales from sales_hourly
            where store_id = @StoreId and business_date between @From::date and @To::date
            """, p)).ToLookup(s => s.BusinessDate.Date);

        var result = new Dictionary<DateTime, Forecast>();
        foreach (var date in list)
        {
            var samples = Enumerable.Range(1, 4).Select(w => date.AddDays(-7 * w))
                .Where(daily.ContainsKey).ToList();

            var byHour = new double[24];
            foreach (var sample in samples)
                foreach (var h in hourly[sample])
                    byHour[h.Hour] += h.Transactions / (double)samples.Count;

            result[date] = new Forecast
            {
                Date = date,
                SampleDays = samples.Count,
                Transactions = samples.Count == 0 ? 0 : Math.Round((decimal)samples.Average(s => daily[s].Transactions), 0),
                Sales = samples.Count == 0 ? 0 : Math.Round(samples.Average(s => daily[s].Sales), 2),
                TransactionsByHour = byHour
            };
        }
        return result;
    }
}
