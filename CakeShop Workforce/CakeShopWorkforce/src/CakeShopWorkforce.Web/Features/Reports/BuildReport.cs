using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Reports;

// =============================================================================
//  REPORTS — every report is the same simple shape: a table (columns + rows).
//  The Reports page shows it, the Excel endpoint saves it, and the browser
//  prints it to PDF. To add a report: add a ReportDefinition + one Build method.
// =============================================================================

/// <summary>How a column is formatted: plain text, a number, money, hours or a percent.</summary>
public enum ColumnKind { Text, Number, Decimal, Money, Hours, Percent }

public sealed record ReportColumn(string Name, ColumnKind Kind = ColumnKind.Text);

public sealed class ReportTable
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<ReportColumn> Columns { get; init; } = [];
    public List<object?[]> Rows { get; init; } = [];
    public object?[]? Totals { get; set; }
    public List<string> Notes { get; init; } = [];

    /// <summary>How a value looks on screen (Excel keeps the raw number).</summary>
    public static string Display(object? value, ColumnKind kind) => value switch
    {
        null => "—",
        decimal d when kind == ColumnKind.Money => Format.Money(d),
        decimal d when kind == ColumnKind.Percent => Format.Percent(d),
        decimal d when kind == ColumnKind.Hours => d.ToString("0.##"),
        decimal d => d.ToString("N1"),
        int i => i.ToString("N0"),
        DateTime dt => dt.ToString("ddd, MMM d"),
        _ => value.ToString() ?? ""
    };
}

public sealed record ReportDefinition(string Key, string Title, string Description, string DefaultPeriod, string Icon);

public static class BuildReport
{
    public static readonly ReportDefinition[] All =
    [
        new("attendance", "Daily personnel attendance", "Each employee per day: schedule, time in/out, late, overtime and status.", "Daily", "clock"),
        new("schedule", "Employee schedule", "Calendar grid for the staff board — print it weekly or monthly.", "Weekly", "calendar"),
        new("transactions", "Daily transactions vs personnel", "Daily KPIs: transactions, sales, ATV, present, TPP, SPP, TPLH, SPLH.", "Monthly", "receipt"),
        new("productivity", "Weekly manpower productivity", "TPLH and SPLH by day against the target, with people per station.", "Weekly", "trend-up"),
        new("monthly", "Monthly manpower analysis", "Week-by-week utilization, gaps, overtime and attendance trends.", "Monthly", "chart"),
        new("ideal", "Ideal vs actual personnel", "Required vs present per day and station, with the staffing status.", "Weekly", "target"),
        new("peak", "Peak hours analysis", "Average traffic and coverage by hour, with recommendations.", "Monthly", "bars"),
        new("timecard", "Timecard summary", "Hours per employee for the payroll provider: regular, overtime, rest-day work.", "Weekly", "document"),
        new("audit", "Audit trail", "Who changed what and when (schedule, attendance, employees, settings).", "Weekly", "history"),
    ];

    public static ReportDefinition? Find(string key) => All.FirstOrDefault(r => r.Key == key);

    public sealed class Handler(DailyFactsLoader loader, GetManpowerAnalysis.Handler analysis, Db db, CurrentStore store)
    {
        public async Task<ReportTable?> HandleAsync(string key, DateTime from, DateTime to)
        {
            from = from.Date;
            to = to.Date;
            if (to < from) (from, to) = (to, from);
            if ((to - from).Days > 92) to = from.AddDays(92);   // keep reports a sensible size

            var range = from == to ? Format.DateLong(from) : $"{from:MMM d, yyyy} – {to:MMM d, yyyy}";
            return key switch
            {
                "attendance" => await AttendanceAsync(from, to, range),
                "schedule" => await ScheduleAsync(from, to, range),
                "transactions" => await TransactionsAsync(from, to, range),
                "productivity" => await ProductivityAsync(from, to, range),
                "monthly" => await MonthlyAsync(from, to, range),
                "ideal" => await IdealAsync(from, to, range),
                "peak" => await PeakAsync(from, to, range),
                "timecard" => await TimecardAsync(from, to, range),
                "audit" => await AuditAsync(from, to, range),
                _ => null
            };
        }

        private async Task<ReportTable> AttendanceAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var table = new ReportTable
            {
                Title = "Daily Personnel Attendance",
                Subtitle = range,
                Columns =
                [
                    new("Date"), new("Employee"), new("ID"), new("Station"), new("Schedule"), new("Time in"), new("Time out"),
                    new("Worked h", ColumnKind.Hours), new("Late min", ColumnKind.Number), new("OT h", ColumnKind.Hours), new("Status")
                ]
            };
            foreach (var day in data.Days)
                foreach (var e in day.Employees)
                {
                    var a = e.Assignment;
                    table.Rows.Add(
                    [
                        day.Date, e.Employee.FullName, e.Employee.EmployeeCode, data.Setup.FindStation(e.StationId)?.Name,
                        a is null ? "Not scheduled" : a.AssignmentType == "Shift" ? Format.TimeRange(a.StartTime, a.EndTime) : a.AssignmentType == "RestDay" ? "Rest day" : a.LeaveType ?? "Leave",
                        e.Attendance?.TimeIn is null ? null : Format.Time(e.Attendance.TimeIn),
                        e.Attendance?.TimeOut is null ? null : Format.Time(e.Attendance.TimeOut),
                        e.Result.WorkedHours, e.Result.LateMinutes, e.Result.OvertimeHours, AttendanceMath.Label(e.Result.Status)
                    ]);
                }
            var s = new PeriodSummary(data.Days);
            table.Totals = ["Total", $"{s.Present} present", null, null, $"{s.Scheduled} scheduled", null, null, s.LaborHours,
                table.Rows.Sum(r => (int)r[8]!), s.OvertimeHours, $"{s.Absent} absent · {s.Late} late"];
            return table;
        }

        private async Task<ReportTable> ScheduleAsync(DateTime from, DateTime to, string range)
        {
            if ((to - from).Days > 30) to = from.AddDays(30);
            var data = await loader.LoadAsync(from, to);
            var dates = Dates.Range(from, to).ToList();
            var table = new ReportTable
            {
                Title = "Employee Schedule",
                Subtitle = range,
                Columns = [new("Employee"), new("Station"), .. dates.Select(d => new ReportColumn($"{Dates.DayCode(d)} {d.Day}")), new("Hours", ColumnKind.Hours)]
            };

            var withRows = data.Assignments.Select(a => a.EmployeeId).ToHashSet();
            foreach (var e in data.Employees.Where(e => e.IsActive || withRows.Contains(e.Id)))
            {
                var row = new List<object?> { e.FullName, data.Setup.FindStation(e.StationId)?.Name };
                decimal hours = 0;
                foreach (var d in dates)
                {
                    var a = data.Assignments.FirstOrDefault(x => x.EmployeeId == e.Id && x.BusinessDate == d);
                    hours += a?.PaidHours ?? 0;
                    row.Add(a switch
                    {
                        null => "",
                        { AssignmentType: "RestDay" } => "Rest",
                        { AssignmentType: "Leave" } => a.LeaveType ?? "Leave",
                        _ => $"{data.Setup.FindTemplate(a.ShiftTemplateId)?.Code ?? "Custom"} {Format.TimeRange(a.StartTime, a.EndTime)}"
                    });
                }
                row.Add(hours);
                table.Rows.Add([.. row]);
            }
            table.Totals = ["Working", null, .. dates.Select(d => (object?)data.Assignments.Count(a => a.BusinessDate == d && a.IsShift)), table.Rows.Sum(r => (decimal)r[^1]!)];
            return table;
        }

        private async Task<ReportTable> TransactionsAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var table = new ReportTable
            {
                Title = "Daily Transactions vs Personnel",
                Subtitle = range,
                Columns =
                [
                    new("Date"), new("Transactions", ColumnKind.Number), new("Sales", ColumnKind.Money), new("ATV", ColumnKind.Money),
                    new("Scheduled", ColumnKind.Number), new("Present", ColumnKind.Number), new("Labor h", ColumnKind.Hours),
                    new("TPP", ColumnKind.Decimal), new("SPP", ColumnKind.Money), new("TPLH", ColumnKind.Decimal), new("SPLH", ColumnKind.Money)
                ]
            };
            foreach (var d in data.Days)
            {
                var m = d.Manpower;
                table.Rows.Add([d.Date, d.HasSales ? d.Transactions : null, d.HasSales ? d.Sales : null, m.AverageTransactionValue,
                    d.Scheduled, d.Present, d.LaborHours, m.TransactionsPerPersonnel, m.SalesPerPersonnel, m.TransactionsPerLaborHour, m.SalesPerLaborHour]);
            }
            var s = new PeriodSummary(data.Days);
            table.Totals = ["Total / average", s.Transactions, s.Sales, s.AverageTransactionValue, s.Scheduled, s.Present, s.LaborHours,
                s.TransactionsPerPersonnel, s.SalesPerPersonnel, s.TransactionsPerLaborHour, s.SalesPerLaborHour];
            table.Notes.Add("TPP = transactions ÷ present. SPP = sales ÷ present. TPLH = transactions ÷ labor hours. SPLH = sales ÷ labor hours. \"—\" = no sales or nobody present.");
            return table;
        }

        private async Task<ReportTable> ProductivityAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var sum = new PeriodSummary(data.Days);
            var typical = sum.MedianTransactionsPerLaborHour ?? 0;
            var table = new ReportTable
            {
                Title = "Weekly Manpower Productivity",
                Subtitle = $"{range} · typical day {typical:0.#} transactions per labor hour",
                Columns =
                [
                    new("Date"), new("Transactions", ColumnKind.Number), new("Labor h", ColumnKind.Hours),
                    new("TPLH", ColumnKind.Decimal), new("SPLH", ColumnKind.Money), new("vs typical day", ColumnKind.Percent),
                    .. data.Setup.Stations.Select(s => new ReportColumn(s.Name, ColumnKind.Number))
                ]
            };
            foreach (var d in data.Days)
            {
                var tplh = d.Manpower.TransactionsPerLaborHour;
                table.Rows.Add([d.Date, d.HasSales ? d.Transactions : null, d.LaborHours, tplh, d.Manpower.SalesPerLaborHour,
                    tplh is null || typical == 0 ? null : Math.Round(tplh.Value * 100 / typical, 0),
                    .. data.Setup.Stations.Select(s => (object?)d.PresentByStation.GetValueOrDefault(s.Id))]);
            }
            table.Totals = ["Period", sum.Transactions, sum.LaborHours, sum.TransactionsPerLaborHour, sum.SalesPerLaborHour,
                sum.TransactionsPerLaborHour is null || typical == 0 ? null : Math.Round(sum.TransactionsPerLaborHour.Value * 100 / typical, 0),
                .. data.Setup.Stations.Select(s => (object?)data.Days.Sum(d => d.PresentByStation.GetValueOrDefault(s.Id)))];
            table.Notes.Add("TPLH counts every labor hour (bakers and decorators too), so days are compared with the period's typical (median) day: 100% = a typical day.");
            table.Notes.Add("Station columns = people present at that station (person-days in the total row).");
            return table;
        }

        private async Task<ReportTable> MonthlyAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var table = new ReportTable
            {
                Title = "Monthly Manpower Analysis",
                Subtitle = range,
                Columns =
                [
                    new("Week of"), new("Days with sales", ColumnKind.Number), new("Transactions", ColumnKind.Number), new("Sales", ColumnKind.Money),
                    new("Present (person-days)", ColumnKind.Number), new("Ideal (person-days)", ColumnKind.Number), new("Gap", ColumnKind.Number),
                    new("Utilization", ColumnKind.Percent), new("TPLH", ColumnKind.Decimal), new("Attendance", ColumnKind.Percent),
                    new("OT h", ColumnKind.Hours), new("Absences", ColumnKind.Number), new("Late", ColumnKind.Number), new("Short days", ColumnKind.Number)
                ]
            };
            foreach (var week in data.Days.GroupBy(d => Dates.StartOfWeek(d.Date)))
                table.Rows.Add(Row(week.Key, new PeriodSummary(week)));
            table.Totals = Row("Whole period", new PeriodSummary(data.Days));
            return table;

            static object?[] Row(object label, PeriodSummary s) =>
            [
                label, s.DaysWithSales, s.Transactions, s.Sales, s.PresentOnSalesDays, s.Ideal, s.Gap, s.Utilization,
                s.TransactionsPerLaborHour, s.AttendanceRate, s.OvertimeHours, s.Absent, s.Late, s.UnderstaffedDays
            ];
        }

        private async Task<ReportTable> IdealAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var table = new ReportTable
            {
                Title = "Ideal vs Actual Personnel",
                Subtitle = range,
                Columns =
                [
                    new("Date"), new("Station"), new("Driver"), new("Required", ColumnKind.Number), new("Present", ColumnKind.Number),
                    new("Gap", ColumnKind.Number), new("Day status"), new("Advice")
                ]
            };
            foreach (var d in data.Days.Where(d => d.HasSales))
            {
                foreach (var st in d.Manpower.Stations)
                    table.Rows.Add([d.Date, st.Station.Name, st.Station.IsTransactionDriven ? "Transactions" : "Fixed", st.Required, st.Present, st.Gap, null, null]);
                table.Rows.Add([d.Date, "All stations", $"Simple formula: {d.Manpower.SimpleIdeal}", d.Manpower.Ideal, d.Present, d.Manpower.Gap,
                    ManpowerMath.Label(d.Manpower.Status), ManpowerMath.Advice(d.Manpower)]);
            }
            if (table.Rows.Count == 0) table.Notes.Add("No days with sales in this range.");
            table.Notes.Add("Required: fixed stations = minimum headcount; Cashier/Sales = MAX(minimum, CEILING(transactions ÷ station target)).");
            return table;
        }

        private async Task<ReportTable> PeakAsync(DateTime from, DateTime to, string range)
        {
            var r = await analysis.HandleAsync(Period.Custom(from, to));
            var table = new ReportTable
            {
                Title = "Peak Hours Analysis",
                Subtitle = $"{r.HourlyFrom:MMM d} – {r.HourlyTo:MMM d, yyyy} · {r.HourlyDays} day(s) with hourly sales",
                Columns =
                [
                    new("Hour"), new("Avg transactions", ColumnKind.Decimal), new("Avg counter on duty", ColumnKind.Decimal),
                    new("Required", ColumnKind.Number), new("Coverage"), new("Tx per person", ColumnKind.Decimal), new("Tag")
                ]
            };
            foreach (var h in r.Hours)
                table.Rows.Add([Format.HourLabel(h.Hour), (decimal)h.AvgTransactions, (decimal)h.AvgPresent, h.Required,
                    h.Coverage?.ToString("0.00"), h.TransactionsPerPerson is null ? null : (decimal)h.TransactionsPerPerson, h.Tag]);
            table.Notes.AddRange(r.Recommendations);
            if (r.HourlyDays == 0) table.Notes.Add("No hourly sales in this range — enter sales by hour to fill this report.");
            return table;
        }

        private async Task<ReportTable> TimecardAsync(DateTime from, DateTime to, string range)
        {
            var data = await loader.LoadAsync(from, to);
            var table = new ReportTable
            {
                Title = "Timecard Summary",
                Subtitle = range,
                Columns =
                [
                    new("Employee"), new("ID"), new("Days worked", ColumnKind.Number), new("Worked h", ColumnKind.Hours),
                    new("Regular h", ColumnKind.Hours), new("OT h", ColumnKind.Hours), new("Rest-day work h", ColumnKind.Hours),
                    new("Late min", ColumnKind.Number), new("Absences", ColumnKind.Number), new("Leave days", ColumnKind.Number),
                    new("Missing time-out", ColumnKind.Number)
                ]
            };
            foreach (var group in data.Days.SelectMany(d => d.Employees).GroupBy(e => e.Employee.Id))
            {
                var emp = group.First().Employee;
                var worked = group.Sum(e => e.Result.WorkedHours);
                var ot = group.Sum(e => e.Result.OvertimeHours);
                table.Rows.Add(
                [
                    emp.FullName, emp.EmployeeCode, group.Count(e => e.Result.IsPresent), worked, worked - ot, ot,
                    group.Where(e => e.Result.Status == AttendanceStatus.RestDayWork).Sum(e => e.Result.WorkedHours),
                    group.Sum(e => e.Result.LateMinutes), group.Count(e => e.Result.Status == AttendanceStatus.Absent),
                    group.Count(e => e.Result.Status == AttendanceStatus.OnLeave), group.Count(e => e.Result.MissingTimeOut)
                ]);
            }
            table.Rows.Sort((a, b) => string.Compare((string)a[0]!, (string)b[0]!, StringComparison.OrdinalIgnoreCase));
            table.Notes.Add("For payroll input only — this app does not compute pay. Rows with a missing time-out have 0 hours for that day until fixed.");
            return table;
        }

        private async Task<ReportTable> AuditAsync(DateTime from, DateTime to, string range)
        {
            await using var conn = await db.OpenAsync();
            var rows = await conn.QueryAsync<(DateTime At, string UserName, string Action, string Entity, string? Details)>("""
                select at, user_name, action, entity, details from audit_log
                where store_id = @StoreId and at >= @From::date and at < (@To::date + 1)
                order by at desc
                limit 5000
                """, new { StoreId = store.Id, From = from, To = to });

            var table = new ReportTable
            {
                Title = "Audit Trail",
                Subtitle = range,
                Columns = [new("When"), new("User"), new("Action"), new("Area"), new("Details")]
            };
            foreach (var r in rows)
                table.Rows.Add([r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), r.UserName, r.Action, r.Entity, r.Details]);
            return table;
        }
    }
}
