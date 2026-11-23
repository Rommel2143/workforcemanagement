using CakeShopWorkforce.Web.Shared;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Settings;

// =============================================================================
//  DEMO DATA — sample employees, schedules, attendance and sales so the app can
//  be tried before real data exists. Every row is flagged is_demo = true, and a
//  banner is shown while it exists, because the spec says sample data must never
//  be mistaken for actual results. "Remove demo data" deletes exactly those rows.
// =============================================================================

public static class GetDemoStatus
{
    public sealed class Handler(Db db, CurrentStore store)
    {
        public async Task<bool> HandleAsync()
        {
            await using var conn = await db.OpenAsync();
            return await conn.ExecuteScalarAsync<bool>("""
                select exists(select 1 from employees where store_id = @StoreId and is_demo)
                    or exists(select 1 from sales_daily where store_id = @StoreId and is_demo)
                """, new { StoreId = store.Id });
        }
    }
}

public static class LoadDemoData
{
    private sealed record DemoPerson(string Name, string Position, string Station, string RestDay, string Type);

    private static readonly DemoPerson[] People =
    [
        new("Maria Santos", "Head Baker", "Production", "Wed", "Full-time"),
        new("Jose Reyes", "Baker", "Production", "Thu", "Full-time"),
        new("Ana Cruz", "Baker", "Production", "Tue", "Full-time"),
        new("Mark Bautista", "Pastry Assistant", "Production", "Mon", "Full-time"),
        new("Liza Garcia", "Cake Decorator", "Decorating", "Mon", "Full-time"),
        new("Paolo Mendoza", "Cake Decorator", "Decorating", "Wed", "Full-time"),
        new("Grace Villanueva", "Decorator Trainee", "Decorating", "Thu", "Part-time"),
        new("Kim Ramos", "Cashier", "Cashier", "Tue", "Full-time"),
        new("Ella Navarro", "Cashier", "Cashier", "Wed", "Full-time"),
        new("Ryan Castillo", "Cashier", "Cashier", "Mon", "Part-time"),
        new("Joy Aquino", "Sales Associate", "Sales", "Thu", "Full-time"),
        new("Carlo Flores", "Sales Associate", "Sales", "Tue", "Full-time"),
        new("Bea Torres", "Sales Associate", "Sales", "Mon", "Full-time"),
        new("Nico Dela Cruz", "Sales Associate", "Sales", "Wed", "Part-time"),
    ];

    // Average transactions by weekday (Mon..Sun) — weekends are busy for a cake shop
    private static readonly int[] BaseTransactions = [150, 140, 150, 165, 215, 300, 270];

    public sealed class Handler(Db db, CurrentStore store, StoreConfig config, Clock clock, AuditLogger audit)
    {
        public async Task<Result> HandleAsync()
        {
            var setup = await config.LoadAsync();
            if (setup.Stations.Count == 0 || setup.ShiftTemplates.Count(t => t.IsActive) == 0)
                return Result.Fail("Add at least one station and one shift template first.");

            await using var conn = await db.OpenAsync();
            var exists = await conn.ExecuteScalarAsync<bool>("select exists(select 1 from employees where store_id = @StoreId and is_demo)", new { StoreId = store.Id });
            if (exists) return Result.Fail("Demo data is already loaded. Remove it first to reload.");

            var random = new Random(2026);      // fixed seed → the same demo every time
            var today = clock.Today;
            var now = clock.Now;
            var from = Dates.StartOfWeek(today).AddDays(-56);   // 8 weeks of history
            var to = Dates.StartOfWeek(today).AddDays(13);      // this week + next week

            var templates = setup.ShiftTemplates.Where(t => t.IsActive).ToList();
            ShiftTemplate Template(string code) => templates.FirstOrDefault(t => t.Code == code) ?? templates[0];
            Station StationFor(string name) => setup.Stations.FirstOrDefault(s => s.Name == name) ?? setup.Stations[0];

            await using var tx = await conn.BeginTransactionAsync();

            // ---- Employees ----
            var employees = new List<(int Id, DemoPerson Person, Station Station, int Index)>();
            for (var i = 0; i < People.Length; i++)
            {
                var person = People[i];
                var station = StationFor(person.Station);
                var id = await conn.ExecuteScalarAsync<int>("""
                    insert into employees (store_id, employee_code, full_name, position, station_id, employment_status, employment_type,
                        contact_number, available_days, max_hours_per_week, rest_day, is_demo)
                    values (@StoreId, @Code, @Name, @Position, @StationId, @Status, @Type, @Phone, @Days, @MaxHours, @RestDay, true)
                    returning id
                    """,
                    new
                    {
                        StoreId = store.Id, Code = $"DEMO-{i + 1:000}", person.Name, person.Position, StationId = station.Id,
                        Status = i % 5 == 4 ? "Probationary" : "Regular", person.Type,
                        Phone = $"0917{random.Next(1000000, 9999999)}",
                        Days = string.Join(',', Dates.DayCodes),
                        MaxHours = person.Type == "Part-time" ? 32 : 48,
                        person.RestDay
                    }, tx);
                employees.Add((id, person, station, i));
            }

            // ---- Schedule + attendance ----
            var assignments = new List<object>();
            var attendance = new List<object>();
            foreach (var date in Dates.Range(from, to))
            {
                var dayCode = Dates.DayCode(date);
                var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                foreach (var (id, person, station, index) in employees)
                {
                    // Part-timers get two more days off (4 working days × 8 h stays under their 32 h maximum)
                    var partTimeOff = person.Type == "Part-time" && (index % 2 == 0 ? dayCode is "Fri" or "Mon" : dayCode is "Tue" or "Thu");
                    if (dayCode == person.RestDay || partTimeOff)
                    {
                        assignments.Add(Row(id, date, "RestDay", null, null, null, null, 0, null));
                        continue;
                    }

                    // About 2% approved leave
                    if (random.NextDouble() < 0.02)
                    {
                        assignments.Add(Row(id, date, "Leave", null, station.Id, null, null, 0, random.Next(3) == 0 ? "Sick" : "Vacation"));
                        continue;
                    }

                    var template = person.Type == "Part-time" ? Template("MID") : person.Station switch
                    {
                        "Production" => Template("OPEN"),
                        "Decorating" => weekend ? Template("FULL") : Template("MID"),
                        // Each cashier keeps one pattern: switching from closing to opening the next day is a "clopening"
                        "Cashier" => index % 2 == 0 ? Template("OPEN") : Template("CLOSE"),
                        _ => (index + date.Day) % 3 == 0 ? Template("CLOSE") : weekend ? Template("CLOSE") : Template("MID")
                    };
                    assignments.Add(Row(id, date, "Shift", template.Id, station.Id, template.StartTime, template.EndTime, template.BreakMinutes, null));

                    // Attendance only for the past (and shifts that already started today)
                    if (date > today || (date == today && template.StartTime > now.TimeOfDay)) continue;

                    if (random.NextDouble() < 0.035)
                    {
                        attendance.Add(new { StoreId = store.Id, EmployeeId = id, Date = date, TimeIn = (TimeSpan?)null, TimeOut = (TimeSpan?)null, Break = 60, Absent = true });
                        continue;
                    }

                    var late = random.NextDouble() < 0.12 ? random.Next(6, 35) : random.Next(-12, 5);
                    var timeIn = template.StartTime + TimeSpan.FromMinutes(late);
                    var timeOut = template.EndTime + TimeSpan.FromMinutes(random.NextDouble() < 0.15 ? random.Next(30, 120) : random.Next(-10, 15));
                    var stillWorking = date == today && template.EndTime > now.TimeOfDay;
                    attendance.Add(new
                    {
                        StoreId = store.Id, EmployeeId = id, Date = date,
                        TimeIn = (TimeSpan?)Trim(timeIn), TimeOut = stillWorking ? null : (TimeSpan?)Trim(timeOut),
                        Break = template.BreakMinutes, Absent = false
                    });
                }
            }

            await conn.ExecuteAsync("""
                insert into schedule_assignments (store_id, employee_id, business_date, assignment_type, shift_template_id, station_id,
                    start_time, end_time, break_minutes, leave_type, is_demo, updated_by)
                values (@StoreId, @EmployeeId, @Date::date, @Type, @TemplateId, @StationId, @Start::time, @End::time, @Break, @LeaveType, true, 'demo')
                on conflict (employee_id, business_date) do nothing
                """, assignments, tx);

            await conn.ExecuteAsync("""
                insert into attendance (store_id, employee_id, business_date, time_in, time_out, break_minutes, is_absent, is_demo, updated_by)
                values (@StoreId, @EmployeeId, @Date::date, @TimeIn::time, @TimeOut::time, @Break, @Absent, true, 'demo')
                on conflict (employee_id, business_date) do nothing
                """, attendance, tx);

            // ---- Sales (past days only, by hour) ----
            var hours = setup.Store.OperatingHours().ToList();
            var hourly = new List<object>();
            var daily = new List<object>();
            foreach (var date in Dates.Range(from, today.AddDays(-1)))
            {
                var dayIndex = ((int)date.DayOfWeek + 6) % 7;
                var total = (int)(BaseTransactions[dayIndex] * (0.85 + random.NextDouble() * 0.3));
                var weights = hours.Select(h => HourWeight(h) * (0.8 + random.NextDouble() * 0.4)).ToList();
                var weightSum = weights.Sum();

                int dayTx = 0;
                decimal daySales = 0;
                for (var i = 0; i < hours.Count; i++)
                {
                    var txCount = (int)Math.Round(total * weights[i] / weightSum);
                    var atv = 320m + (decimal)(random.NextDouble() * 120) + (dayIndex >= 5 ? 60 : 0);   // bigger cakes on weekends
                    var sales = Math.Round(txCount * atv, 2);
                    dayTx += txCount;
                    daySales += sales;
                    hourly.Add(new { StoreId = store.Id, Date = date, Hour = hours[i], Tx = txCount, Sales = sales });
                }
                daily.Add(new { StoreId = store.Id, Date = date, Tx = dayTx, Sales = daySales });
            }

            await conn.ExecuteAsync("""
                insert into sales_hourly (store_id, business_date, hour, transactions, sales, is_demo)
                values (@StoreId, @Date::date, @Hour, @Tx, @Sales, true)
                on conflict (store_id, business_date, hour) do nothing
                """, hourly, tx);
            await conn.ExecuteAsync("""
                insert into sales_daily (store_id, business_date, transactions, sales, is_demo)
                values (@StoreId, @Date::date, @Tx, @Sales, true)
                on conflict (store_id, business_date) do nothing
                """, daily, tx);

            await tx.CommitAsync();
            await audit.WriteAsync(conn, "Load demo data", "Settings", null,
                $"{employees.Count} employees, {assignments.Count} schedule entries, {attendance.Count} attendance rows, {daily.Count} sales days");
            return Result.Success();

            object Row(int employeeId, DateTime date, string type, int? templateId, int? stationId, TimeSpan? start, TimeSpan? end, int breakMinutes, string? leaveType) =>
                new { StoreId = store.Id, EmployeeId = employeeId, Date = date, Type = type, TemplateId = templateId, StationId = stationId, Start = start, End = end, Break = breakMinutes, LeaveType = leaveType };
        }

        /// <summary>Lunch (11–1) and after-work (4–7) peaks.</summary>
        private static double HourWeight(int hour) => hour switch
        {
            < 9 => 0.4,
            9 or 10 => 0.8,
            11 or 12 or 13 => 1.5,
            14 or 15 => 0.9,
            16 or 17 or 18 => 1.6,
            19 => 1.0,
            _ => 0.5
        };

        private static TimeSpan Trim(TimeSpan t) => new(t.Hours, t.Minutes, 0);
    }
}

public static class RemoveDemoData
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<int> HandleAsync()
        {
            await using var conn = await db.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var p = new { StoreId = store.Id };

            const string demoEmployees = "select id from employees where store_id = @StoreId and is_demo";
            var removed = 0;
            removed += await conn.ExecuteAsync($"update users set employee_id = null where employee_id in ({demoEmployees})", p, tx);
            removed += await conn.ExecuteAsync($"delete from attendance where store_id = @StoreId and (is_demo or employee_id in ({demoEmployees}))", p, tx);
            removed += await conn.ExecuteAsync($"delete from schedule_assignments where store_id = @StoreId and (is_demo or employee_id in ({demoEmployees}))", p, tx);
            removed += await conn.ExecuteAsync("delete from employees where store_id = @StoreId and is_demo", p, tx);
            removed += await conn.ExecuteAsync("delete from sales_hourly where store_id = @StoreId and is_demo", p, tx);
            removed += await conn.ExecuteAsync("delete from sales_daily where store_id = @StoreId and is_demo", p, tx);
            await tx.CommitAsync();

            await audit.WriteAsync(conn, "Remove demo data", "Settings", null, $"{removed} rows removed");
            return removed;
        }
    }
}
