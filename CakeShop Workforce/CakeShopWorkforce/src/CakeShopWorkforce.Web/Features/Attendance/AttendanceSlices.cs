using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Attendance;

// =============================================================================
//  ATTENDANCE FEATURE — slices
//     GetDailyAttendance → everyone scheduled (or clocked in) on a date, with status
//     SaveAttendance     → save time in / time out / absent for one person and day
//     FillScheduledTimes → copy scheduled start/end into empty rows (quick entry)
// =============================================================================

public static class GetDailyAttendance
{
    public sealed class Result
    {
        public DateTime Date { get; init; }
        public required DayFacts Day { get; init; }
        public required StoreSetup Setup { get; init; }

        /// <summary>Active employees with nothing on this date (can be added as unscheduled work).</summary>
        public List<EmployeeInfo> Others { get; init; } = [];
    }

    public sealed class Handler(DailyFactsLoader loader)
    {
        public async Task<Result> HandleAsync(DateTime date)
        {
            var data = await loader.LoadAsync(date, date);
            var day = data.Days[0];
            var listed = day.Employees.Select(e => e.Employee.Id).ToHashSet();

            return new Result
            {
                Date = date.Date,
                Day = day,
                Setup = data.Setup,
                Others = data.Employees.Where(e => e.IsActive && !listed.Contains(e.Id)).ToList()
            };
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveAttendance
{
    public sealed class Command
    {
        public int EmployeeId { get; set; }
        public DateTime Date { get; set; }
        public TimeSpan? TimeIn { get; set; }
        public TimeSpan? TimeOut { get; set; }
        public int BreakMinutes { get; set; } = 60;
        public bool IsAbsent { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, CurrentUser user, Clock clock)
    {
        public async Task<Result> HandleAsync(Command cmd)
        {
            if (cmd.Date.Date > clock.Today)
                return Result.Fail("You can't record attendance for a future date.");
            if (cmd.TimeOut is not null && cmd.TimeIn is null)
                return Result.Fail("Enter the time in first.");
            if (cmd.IsAbsent)
            {
                cmd.TimeIn = null;
                cmd.TimeOut = null;
            }

            await using var conn = await db.OpenAsync();
            var p = new
            {
                StoreId = store.Id, cmd.EmployeeId, Date = cmd.Date.Date, cmd.TimeIn, cmd.TimeOut,
                BreakMinutes = Math.Clamp(cmd.BreakMinutes, 0, 600), cmd.IsAbsent,
                Note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim(),
                User = await user.GetNameAsync()
            };

            // Nothing left to record → remove the row (back to "not in yet" / "absent" by schedule)
            if (cmd.TimeIn is null && !cmd.IsAbsent && p.Note is null)
            {
                await conn.ExecuteAsync("""
                    delete from attendance where store_id = @StoreId and employee_id = @EmployeeId and business_date = @Date::date
                    """, p);
            }
            else
            {
                await conn.ExecuteAsync("""
                    insert into attendance (store_id, employee_id, business_date, time_in, time_out, break_minutes, is_absent, note, updated_at, updated_by)
                    values (@StoreId, @EmployeeId, @Date::date, @TimeIn::time, @TimeOut::time, @BreakMinutes, @IsAbsent, @Note, now(), @User)
                    on conflict (employee_id, business_date) do update set
                        time_in = excluded.time_in, time_out = excluded.time_out, break_minutes = excluded.break_minutes,
                        is_absent = excluded.is_absent, note = excluded.note, updated_at = now(), updated_by = excluded.updated_by
                    """, p);
            }

            var detail = cmd.IsAbsent ? "Absent" : $"{Format.Time(cmd.TimeIn)} – {Format.Time(cmd.TimeOut)}";
            await audit.WriteAsync(conn, "Attendance", "Attendance", cmd.EmployeeId, $"{cmd.Date:ddd MMM d}: {detail}");
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class FillScheduledTimes
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, CurrentUser user, Clock clock)
    {
        /// <summary>
        /// For everyone with a shift and no attendance row yet, record the scheduled start/end
        /// as their time in/out. Today: only shifts that have already ended.
        /// </summary>
        public async Task<int> HandleAsync(DateTime date)
        {
            if (date.Date > clock.Today) return 0;
            await using var conn = await db.OpenAsync();

            var filled = await conn.ExecuteAsync("""
                insert into attendance (store_id, employee_id, business_date, time_in, time_out, break_minutes, updated_by)
                select a.store_id, a.employee_id, a.business_date, a.start_time, a.end_time, a.break_minutes, @User
                from schedule_assignments a
                where a.store_id = @StoreId and a.business_date = @Date::date
                  and a.assignment_type = 'Shift' and a.start_time is not null
                  and (@IsPast or a.end_time <= @Now::time)
                on conflict (employee_id, business_date) do nothing
                """,
                new { StoreId = store.Id, Date = date.Date, IsPast = date.Date < clock.Today, Now = clock.Now.TimeOfDay, User = await user.GetNameAsync() });

            if (filled > 0)
                await audit.WriteAsync(conn, "Fill scheduled times", "Attendance", null, $"{date:ddd MMM d}: {filled} rows");
            return filled;
        }
    }
}
