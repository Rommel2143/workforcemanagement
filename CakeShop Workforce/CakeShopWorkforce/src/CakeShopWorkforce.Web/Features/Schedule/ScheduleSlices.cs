using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;
using Dapper;
using Npgsql;

namespace CakeShopWorkforce.Web.Features.Schedule;

// =============================================================================
//  SCHEDULE FEATURE — slices
//     GetWeekSchedule   → the week grid (employees × 7 days) + warnings + forecast
//     GetMonthSchedule  → month calendar summary
//     SaveAssignment    → put a shift / rest day / leave in one cell (insert or update)
//     ClearAssignment   → empty one cell
//     CopyWeek          → copy last week into this week (empty cells only)
//     FillRestDays      → put each employee's usual rest day in this week (empty cells only)
//     PublishWeek       → mark the week as published (final for staff)
//
//  Every change writes to the audit log, and a change to a PUBLISHED week flags
//  the week as "changed after publish" so the manager knows to re-publish.
// =============================================================================

/// <summary>Draft / Published state of one week.</summary>
public sealed class WeekStatus
{
    public string Status { get; set; } = "Draft";
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public bool ChangedAfterPublish { get; set; }

    public bool IsPublished => Status == "Published";
}

internal static class ScheduleSql
{
    public static async Task<WeekStatus> GetWeekStatusAsync(NpgsqlConnection conn, int storeId, DateTime weekStart) =>
        await conn.QuerySingleOrDefaultAsync<WeekStatus>("""
            select status, published_at, published_by, changed_after_publish
            from schedule_weeks where store_id = @storeId and week_start = @weekStart::date
            """, new { storeId, weekStart }) ?? new WeekStatus();

    /// <summary>Editing a published week flags it so the manager re-publishes (and staff know it changed).</summary>
    public static Task MarkChangedAsync(NpgsqlConnection conn, int storeId, DateTime anyDateInWeek) =>
        conn.ExecuteAsync("""
            update schedule_weeks set changed_after_publish = true
            where store_id = @storeId and week_start = @weekStart::date and status = 'Published'
            """, new { storeId, weekStart = Dates.StartOfWeek(anyDateInWeek) });
}

// -----------------------------------------------------------------------------
public static class GetWeekSchedule
{
    public sealed class Result
    {
        public DateTime WeekStart { get; init; }
        public List<DateTime> Days { get; init; } = [];
        public required StoreSetup Setup { get; init; }
        public List<EmployeeInfo> Employees { get; init; } = [];
        public Dictionary<(int EmployeeId, DateTime Date), AssignmentRow> Cells { get; init; } = [];
        public WeekStatus Status { get; init; } = new();
        public List<ScheduleWarning> Warnings { get; init; } = [];
        public Dictionary<DateTime, Forecast> Forecasts { get; init; } = [];

        public AssignmentRow? Cell(int employeeId, DateTime date) => Cells.GetValueOrDefault((employeeId, date.Date));

        public decimal WeekHours(int employeeId) => Days.Sum(d => Cell(employeeId, d)?.PaidHours ?? 0);

        public int Headcount(DateTime date) => Cells.Values.Count(c => c.BusinessDate == date && c.IsShift);

        public int StationCount(DateTime date, int stationId) =>
            Cells.Values.Count(c => c.BusinessDate == date && c.IsShift && c.StationId == stationId);

        /// <summary>Forecast ideal headcount for the day (station-aware), or null without history.</summary>
        public int? Ideal(DateTime date) =>
            Forecasts.TryGetValue(date, out var f) && f.HasData ? ManpowerMath.IdealForTransactions(Setup.Stations, f.Transactions) : null;
    }

    public sealed class Handler(DailyFactsLoader loader, Db db, CurrentStore store)
    {
        public async Task<Result> HandleAsync(DateTime anyDate)
        {
            var weekStart = Dates.StartOfWeek(anyDate);
            var weekEnd = weekStart.AddDays(6);

            // The 6 days before the week are loaded too, to catch "7 days in a row" streaks across weeks
            var data = await loader.LoadAsync(weekStart.AddDays(-6), weekEnd);
            var weekRows = data.Assignments.Where(a => a.BusinessDate >= weekStart).ToList();

            // Active employees + anyone (even inactive) who still has something this week
            var withRows = weekRows.Select(r => r.EmployeeId).ToHashSet();
            var employees = data.Employees.Where(e => e.IsActive || withRows.Contains(e.Id))
                .OrderBy(e => data.Setup.Stations.FindIndex(s => s.Id == e.StationId) is var i && i < 0 ? 99 : i)
                .ThenBy(e => e.FullName)
                .ToList();

            var warnings = ScheduleRules.Check(
                weekStart,
                data.Assignments.Select(a => a.ToPlanned()).ToList(),
                employees.Select(e => new PlannedEmployee
                {
                    Id = e.Id, Name = e.FullName, MaxHoursPerWeek = e.MaxHoursPerWeek,
                    AvailableDays = e.AvailableDays, RestDay = e.RestDay, IsActive = e.IsActive
                }).ToList(),
                data.Setup.Stations,
                data.Setup.Store.RegularHoursPerDay);

            await using var conn = await db.OpenAsync();
            var status = await ScheduleSql.GetWeekStatusAsync(conn, store.Id, weekStart);

            var days = Dates.Range(weekStart, weekEnd).ToList();
            return new Result
            {
                WeekStart = weekStart,
                Days = days,
                Setup = data.Setup,
                Employees = employees,
                Cells = weekRows.ToDictionary(r => (r.EmployeeId, r.BusinessDate.Date)),
                Status = status,
                Warnings = warnings.OrderBy(w => w.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ThenBy(w => w.Date).ToList(),
                Forecasts = await loader.ForecastAsync(days)
            };
        }
    }
}

// -----------------------------------------------------------------------------
public static class GetMonthSchedule
{
    public sealed class DaySummary
    {
        public DateTime Date { get; init; }
        public int Shifts { get; init; }
        public int RestDays { get; init; }
        public int Leaves { get; init; }
        public List<(Station Station, int Count)> Stations { get; init; } = [];
        public List<string> ShortStations => Stations.Where(s => Shifts > 0 && s.Count < s.Station.MinHeadcount).Select(s => s.Station.Name).ToList();
        public List<(string Code, string Color, int Count)> Templates { get; init; } = [];
    }

    public sealed class Result
    {
        public DateTime MonthStart { get; init; }
        public List<DaySummary> Days { get; init; } = [];
        public Dictionary<DateTime, WeekStatus> Weeks { get; init; } = [];
    }

    public sealed class Handler(Db db, CurrentStore store, StoreConfig config)
    {
        public async Task<Result> HandleAsync(DateTime anyDate)
        {
            var monthStart = Dates.StartOfMonth(anyDate);
            var gridStart = Dates.StartOfWeek(monthStart);
            var gridEnd = Dates.StartOfWeek(monthStart.AddMonths(1).AddDays(-1)).AddDays(6);
            var setup = await config.LoadAsync();

            await using var conn = await db.OpenAsync();
            var rows = (await conn.QueryAsync<AssignmentRow>("""
                select a.* from schedule_assignments a
                join employees e on e.id = a.employee_id
                where a.store_id = @StoreId and a.business_date between @From::date and @To::date
                """, new { StoreId = store.Id, From = gridStart, To = gridEnd })).ToList();

            var weeks = (await conn.QueryAsync<(DateTime WeekStart, string Status, bool Changed)>("""
                select week_start, status, changed_after_publish from schedule_weeks
                where store_id = @StoreId and week_start between @From::date and @To::date
                """, new { StoreId = store.Id, From = gridStart, To = gridEnd }))
                .ToDictionary(w => w.WeekStart.Date, w => new WeekStatus { Status = w.Status, ChangedAfterPublish = w.Changed });

            var byDate = rows.ToLookup(r => r.BusinessDate.Date);
            var days = Dates.Range(gridStart, gridEnd).Select(d =>
            {
                var dayRows = byDate[d].ToList();
                return new DaySummary
                {
                    Date = d,
                    Shifts = dayRows.Count(r => r.IsShift),
                    RestDays = dayRows.Count(r => r.AssignmentType == "RestDay"),
                    Leaves = dayRows.Count(r => r.AssignmentType == "Leave"),
                    Stations = setup.Stations.Select(s => (s, dayRows.Count(r => r.IsShift && r.StationId == s.Id))).ToList(),
                    Templates = setup.ShiftTemplates
                        .Select(t => (t.Code, t.Color, dayRows.Count(r => r.IsShift && r.ShiftTemplateId == t.Id)))
                        .Where(x => x.Item3 > 0).ToList()
                };
            }).ToList();

            return new Result { MonthStart = monthStart, Days = days, Weeks = weeks };
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveAssignment
{
    public static readonly string[] LeaveTypes = ["Vacation", "Sick", "Emergency", "Service Incentive", "Maternity/Paternity", "Unpaid"];

    public sealed class Command
    {
        public int EmployeeId { get; set; }
        public DateTime Date { get; set; }
        public string Type { get; set; } = "Shift";          // Shift, RestDay, Leave
        public int? TemplateId { get; set; }
        public int? StationId { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public int BreakMinutes { get; set; } = -1;          // -1 = use the template's break
        public string? LeaveType { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Handler(Db db, CurrentStore store, StoreConfig config, AuditLogger audit, CurrentUser user)
    {
        public async Task<Result> HandleAsync(Command cmd)
        {
            await using var conn = await db.OpenAsync();

            var employee = await conn.QuerySingleOrDefaultAsync<(string FullName, bool IsActive, int? StationId)>(
                "select full_name, is_active, station_id from employees where id = @EmployeeId and store_id = @StoreId",
                new { cmd.EmployeeId, StoreId = store.Id });
            if (employee.FullName is null) return Result.Fail("Employee not found.");

            // BLOCK rule (spec 10.2): inactive employees cannot be scheduled
            if (!employee.IsActive && cmd.Type == "Shift")
                return Result.Fail($"{employee.FullName} is inactive and cannot be scheduled.");

            string label;
            if (cmd.Type == "Shift")
            {
                var setup = await config.LoadAsync();
                var template = setup.FindTemplate(cmd.TemplateId);
                cmd.StartTime ??= template?.StartTime;
                cmd.EndTime ??= template?.EndTime;
                if (cmd.BreakMinutes < 0) cmd.BreakMinutes = template?.BreakMinutes ?? 60;
                cmd.StationId ??= employee.StationId;

                if (cmd.StartTime is null || cmd.EndTime is null)
                    return Result.Fail("Pick a shift template or enter a start and end time.");
                if (cmd.StartTime == cmd.EndTime)
                    return Result.Fail("Start and end time cannot be the same.");

                label = $"{template?.Code ?? "Custom"} {Format.TimeRange(cmd.StartTime, cmd.EndTime)}";
                cmd.LeaveType = null;
            }
            else
            {
                cmd.TemplateId = null;
                cmd.StartTime = null;
                cmd.EndTime = null;
                cmd.BreakMinutes = 0;
                if (cmd.Type == "RestDay") cmd.LeaveType = null;
                if (cmd.Type == "Leave") cmd.LeaveType ??= "Vacation";
                label = cmd.Type == "Leave" ? $"Leave ({cmd.LeaveType})" : "Rest day";
            }

            await conn.ExecuteAsync("""
                insert into schedule_assignments (store_id, employee_id, business_date, assignment_type, shift_template_id,
                    station_id, start_time, end_time, break_minutes, leave_type, note, updated_at, updated_by)
                values (@StoreId, @EmployeeId, @Date::date, @Type, @TemplateId,
                    @StationId, @StartTime::time, @EndTime::time, @BreakMinutes, @LeaveType, @Note, now(), @User)
                on conflict (employee_id, business_date) do update set
                    assignment_type = excluded.assignment_type, shift_template_id = excluded.shift_template_id,
                    station_id = excluded.station_id, start_time = excluded.start_time, end_time = excluded.end_time,
                    break_minutes = excluded.break_minutes, leave_type = excluded.leave_type, note = excluded.note,
                    updated_at = now(), updated_by = excluded.updated_by
                """,
                new
                {
                    StoreId = store.Id, cmd.EmployeeId, Date = cmd.Date.Date, cmd.Type, cmd.TemplateId, cmd.StationId,
                    cmd.StartTime, cmd.EndTime, BreakMinutes = Math.Max(0, cmd.BreakMinutes), cmd.LeaveType,
                    Note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim(),
                    User = await user.GetNameAsync()
                });

            await ScheduleSql.MarkChangedAsync(conn, store.Id, cmd.Date);
            await audit.WriteAsync(conn, "Assign", "Schedule", cmd.EmployeeId, $"{employee.FullName} · {cmd.Date:ddd MMM d} · {label}");
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class ClearAssignment
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(int employeeId, DateTime date)
        {
            await using var conn = await db.OpenAsync();
            var removed = await conn.ExecuteAsync("""
                delete from schedule_assignments
                where store_id = @StoreId and employee_id = @employeeId and business_date = @date::date
                """, new { StoreId = store.Id, employeeId, date });

            if (removed > 0)
            {
                await ScheduleSql.MarkChangedAsync(conn, store.Id, date);
                await audit.WriteAsync(conn, "Clear", "Schedule", employeeId, $"{date:ddd MMM d}");
            }
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class CopyWeek
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, CurrentUser user)
    {
        /// <summary>Copies the source week into the target week. Only fills EMPTY cells of ACTIVE employees.</summary>
        public async Task<int> HandleAsync(DateTime sourceWeekStart, DateTime targetWeekStart)
        {
            var offset = (targetWeekStart.Date - sourceWeekStart.Date).Days;
            await using var conn = await db.OpenAsync();

            var copied = await conn.ExecuteAsync("""
                insert into schedule_assignments (store_id, employee_id, business_date, assignment_type, shift_template_id,
                    station_id, start_time, end_time, break_minutes, leave_type, note, updated_at, updated_by)
                select a.store_id, a.employee_id, a.business_date + @offset, a.assignment_type, a.shift_template_id,
                    a.station_id, a.start_time, a.end_time, a.break_minutes, null, null, now(), @User
                from schedule_assignments a
                join employees e on e.id = a.employee_id and e.is_active
                where a.store_id = @StoreId
                  and a.business_date between @From::date and @To::date
                  and a.assignment_type <> 'Leave'           -- leave is personal, never copied
                on conflict (employee_id, business_date) do nothing
                """,
                new { StoreId = store.Id, offset, From = sourceWeekStart, To = sourceWeekStart.AddDays(6), User = await user.GetNameAsync() });

            if (copied > 0)
            {
                await ScheduleSql.MarkChangedAsync(conn, store.Id, targetWeekStart);
                await audit.WriteAsync(conn, "Copy week", "Schedule", null, $"{sourceWeekStart:MMM d} → {targetWeekStart:MMM d}: {copied} entries");
            }
            return copied;
        }
    }
}

// -----------------------------------------------------------------------------
public static class FillRestDays
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, CurrentUser user)
    {
        /// <summary>Puts each active employee's usual rest day into the week, where the cell is empty.</summary>
        public async Task<int> HandleAsync(DateTime weekStart)
        {
            await using var conn = await db.OpenAsync();
            var employees = await conn.QueryAsync<(int Id, string RestDay)>("""
                select id, rest_day from employees where store_id = @StoreId and is_active and rest_day is not null
                """, new { StoreId = store.Id });

            var userName = await user.GetNameAsync();
            var added = 0;
            foreach (var (id, restDay) in employees)
            {
                var index = Array.IndexOf(Dates.DayCodes, restDay);
                if (index < 0) continue;
                added += await conn.ExecuteAsync("""
                    insert into schedule_assignments (store_id, employee_id, business_date, assignment_type, updated_by)
                    values (@StoreId, @id, @Date::date, 'RestDay', @userName)
                    on conflict (employee_id, business_date) do nothing
                    """, new { StoreId = store.Id, id, Date = weekStart.AddDays(index), userName });
            }

            if (added > 0)
            {
                await ScheduleSql.MarkChangedAsync(conn, store.Id, weekStart);
                await audit.WriteAsync(conn, "Fill rest days", "Schedule", null, $"Week of {weekStart:MMM d}: {added} rest days");
            }
            return added;
        }
    }
}

// -----------------------------------------------------------------------------
public static class PublishWeek
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, CurrentUser user)
    {
        public async Task<Result> HandleAsync(DateTime weekStart)
        {
            await using var conn = await db.OpenAsync();
            var hasShifts = await conn.ExecuteScalarAsync<bool>("""
                select exists(select 1 from schedule_assignments
                              where store_id = @StoreId and business_date between @From::date and @To::date)
                """, new { StoreId = store.Id, From = weekStart, To = weekStart.AddDays(6) });
            if (!hasShifts) return Result.Fail("This week is empty. Add shifts before publishing.");

            await conn.ExecuteAsync("""
                insert into schedule_weeks (store_id, week_start, status, published_at, published_by, changed_after_publish)
                values (@StoreId, @WeekStart::date, 'Published', now(), @User, false)
                on conflict (store_id, week_start) do update set
                    status = 'Published', published_at = now(), published_by = excluded.published_by, changed_after_publish = false
                """, new { StoreId = store.Id, WeekStart = weekStart, User = await user.GetNameAsync() });

            await audit.WriteAsync(conn, "Publish", "Schedule", null, $"Week of {weekStart:MMM d, yyyy}");
            return Result.Success();
        }
    }
}
