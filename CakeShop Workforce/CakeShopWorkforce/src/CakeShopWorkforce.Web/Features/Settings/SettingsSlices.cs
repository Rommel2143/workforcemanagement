using System.ComponentModel.DataAnnotations;
using CakeShopWorkforce.Web.Features.Auth;
using CakeShopWorkforce.Web.Shared;
using Dapper;
using Microsoft.AspNetCore.Identity;

namespace CakeShopWorkforce.Web.Features.Settings;

// =============================================================================
//  SETTINGS FEATURE — the only place that changes the store's configuration.
//     SaveStore / SaveStation / DeleteStation / SaveShiftTemplate
//     GetUsers / SaveUser / ResetPassword       (Owner only)
//     GetAuditLog
// =============================================================================

public static class SaveStore
{
    public sealed class Form
    {
        [Required, StringLength(80)] public string Name { get; set; } = "";
        public TimeOnly OpenTime { get; set; }
        public TimeOnly CloseTime { get; set; }
        [Range(0, 60)] public int GraceMinutes { get; set; }
        [Range(1, 12)] public decimal RegularHoursPerDay { get; set; }
        [Range(1, 1000)] public int TargetTxPerPersonnel { get; set; }
        [Range(1, 200)] public int TargetTxPerPersonHour { get; set; }
        [Range(0, 60)] public decimal WeeklyOtAlertHours { get; set; }

        public static Form From(StoreSettings s) => new()
        {
            Name = s.Name,
            OpenTime = TimeOnly.FromTimeSpan(s.OpenTime),
            CloseTime = TimeOnly.FromTimeSpan(s.CloseTime),
            GraceMinutes = s.GraceMinutes,
            RegularHoursPerDay = s.RegularHoursPerDay,
            TargetTxPerPersonnel = s.TargetTxPerPersonnel,
            TargetTxPerPersonHour = s.TargetTxPerPersonHour,
            WeeklyOtAlertHours = s.WeeklyOtAlertHours
        };
    }

    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Form form)
        {
            if (form.OpenTime == form.CloseTime) return Result.Fail("Opening and closing time cannot be the same.");
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync("""
                update stores set name = @Name, open_time = @OpenTime::time, close_time = @CloseTime::time,
                    grace_minutes = @GraceMinutes, regular_hours_per_day = @RegularHoursPerDay,
                    target_tx_per_personnel = @TargetTxPerPersonnel, target_tx_per_person_hour = @TargetTxPerPersonHour,
                    weekly_ot_alert_hours = @WeeklyOtAlertHours
                where id = @StoreId
                """,
                new
                {
                    StoreId = store.Id, Name = form.Name.Trim(), OpenTime = form.OpenTime.ToTimeSpan(), CloseTime = form.CloseTime.ToTimeSpan(),
                    form.GraceMinutes, form.RegularHoursPerDay, form.TargetTxPerPersonnel, form.TargetTxPerPersonHour, form.WeeklyOtAlertHours
                });
            await audit.WriteAsync(conn, "Edit store settings", "Settings", store.Id, form.Name);
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveStation
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Station s)
        {
            if (string.IsNullOrWhiteSpace(s.Name)) return Result.Fail("Station name is required.");
            if (s.MinHeadcount < 0) return Result.Fail("Minimum headcount cannot be negative.");
            if (s.IsTransactionDriven && s.TargetTxPerPerson is not > 0)
                return Result.Fail("Transaction-driven stations need a target (transactions per person per day).");
            if (!s.IsTransactionDriven) s.TargetTxPerPerson = null;

            await using var conn = await db.OpenAsync();
            var p = new { StoreId = store.Id, s.Id, Name = s.Name.Trim(), s.Color, s.Driver, s.TargetTxPerPerson, s.MinHeadcount, s.SortOrder };
            if (s.Id == 0)
                await conn.ExecuteAsync("""
                    insert into stations (store_id, name, color, driver, target_tx_per_person, min_headcount, sort_order)
                    values (@StoreId, @Name, @Color, @Driver, @TargetTxPerPerson, @MinHeadcount,
                            (select coalesce(max(sort_order), 0) + 1 from stations where store_id = @StoreId))
                    """, p);
            else
                await conn.ExecuteAsync("""
                    update stations set name = @Name, color = @Color, driver = @Driver, target_tx_per_person = @TargetTxPerPerson,
                        min_headcount = @MinHeadcount
                    where id = @Id and store_id = @StoreId
                    """, p);

            await audit.WriteAsync(conn, s.Id == 0 ? "Add station" : "Edit station", "Settings", s.Id == 0 ? null : s.Id,
                $"{p.Name}: {s.Driver}, min {s.MinHeadcount}{(s.TargetTxPerPerson is null ? "" : $", 1 per {s.TargetTxPerPerson} tx")}");
            return Result.Success();
        }
    }
}

public static class DeleteStation
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Station s)
        {
            await using var conn = await db.OpenAsync();
            var used = await conn.ExecuteScalarAsync<bool>("""
                select exists(select 1 from employees where station_id = @Id)
                    or exists(select 1 from schedule_assignments where station_id = @Id)
                """, new { s.Id });
            if (used) return Result.Fail($"{s.Name} is used by employees or schedules. Move them to another station first.");

            await conn.ExecuteAsync("delete from stations where id = @Id and store_id = @StoreId", new { s.Id, StoreId = store.Id });
            await audit.WriteAsync(conn, "Delete station", "Settings", s.Id, s.Name);
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveShiftTemplate
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(ShiftTemplate t)
        {
            if (string.IsNullOrWhiteSpace(t.Code) || string.IsNullOrWhiteSpace(t.Name)) return Result.Fail("Code and name are required.");
            if (t.StartTime == t.EndTime) return Result.Fail("Start and end time cannot be the same.");
            if (t.BreakMinutes is < 0 or > 240) return Result.Fail("Break must be between 0 and 240 minutes.");

            await using var conn = await db.OpenAsync();
            var p = new
            {
                StoreId = store.Id, t.Id, Code = t.Code.Trim().ToUpperInvariant(), Name = t.Name.Trim(),
                t.StartTime, t.EndTime, t.BreakMinutes, t.Color, t.IsActive
            };
            if (t.Id == 0)
                await conn.ExecuteAsync("""
                    insert into shift_templates (store_id, code, name, start_time, end_time, break_minutes, color, is_active, sort_order)
                    values (@StoreId, @Code, @Name, @StartTime::time, @EndTime::time, @BreakMinutes, @Color, @IsActive,
                            (select coalesce(max(sort_order), 0) + 1 from shift_templates where store_id = @StoreId))
                    """, p);
            else
                await conn.ExecuteAsync("""
                    update shift_templates set code = @Code, name = @Name, start_time = @StartTime::time, end_time = @EndTime::time,
                        break_minutes = @BreakMinutes, color = @Color, is_active = @IsActive
                    where id = @Id and store_id = @StoreId
                    """, p);

            await audit.WriteAsync(conn, t.Id == 0 ? "Add shift template" : "Edit shift template", "Settings", t.Id == 0 ? null : t.Id,
                $"{p.Code} {Format.TimeRange(t.StartTime, t.EndTime)}{(t.IsActive ? "" : " (inactive)")}");
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public sealed class UserRow
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public bool IsActive { get; set; }
}

public static class GetUsers
{
    public sealed class Handler(Db db)
    {
        public async Task<List<UserRow>> HandleAsync()
        {
            await using var conn = await db.OpenAsync();
            var rows = await conn.QueryAsync<UserRow>("""
                select u.id, u.user_name, u.display_name, u.role, u.employee_id, e.full_name as employee_name, u.is_active
                from users u left join employees e on e.id = u.employee_id
                order by u.is_active desc, u.display_name
                """);
            return rows.ToList();
        }
    }
}

public static class SaveUser
{
    public sealed class Form
    {
        public int? Id { get; set; }
        [Required, StringLength(40), RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Letters, numbers, dot, dash or underscore only")]
        public string UserName { get; set; } = "";
        [Required, StringLength(80)] public string DisplayName { get; set; } = "";
        [Required] public string Role { get; set; } = Roles.Staff;
        public int? EmployeeId { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Only for new users.</summary>
        public string? Password { get; set; }
    }

    public sealed class Handler(Db db, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Form form)
        {
            if (!Roles.All.Contains(form.Role)) return Result.Fail("Unknown role.");
            if (form.Id is null && !PasswordRules.IsValid(form.Password, out var why)) return Result.Fail(why);

            await using var conn = await db.OpenAsync();
            var taken = await conn.ExecuteScalarAsync<bool>(
                "select exists(select 1 from users where lower(user_name) = lower(@UserName) and id <> coalesce(@Id, 0))", form);
            if (taken) return Result.Fail($"User name {form.UserName} is already taken.");

            // Never lock everyone out: keep at least one active Owner
            if (form.Id is not null && (form.Role != Roles.Owner || !form.IsActive))
            {
                var otherOwners = await conn.ExecuteScalarAsync<int>(
                    "select count(*) from users where role = 'Owner' and is_active and id <> @Id", form);
                var wasOwner = await conn.ExecuteScalarAsync<bool>("select role = 'Owner' from users where id = @Id", form);
                if (wasOwner && otherOwners == 0) return Result.Fail("There must be at least one active Owner.");
            }

            if (form.Id is null)
            {
                var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), form.Password!);
                await conn.ExecuteAsync("""
                    insert into users (user_name, display_name, password_hash, role, employee_id, is_active)
                    values (@UserName, @DisplayName, @hash, @Role, @EmployeeId, @IsActive)
                    """, new { UserName = form.UserName.Trim(), DisplayName = form.DisplayName.Trim(), hash, form.Role, form.EmployeeId, form.IsActive });
            }
            else
            {
                await conn.ExecuteAsync("""
                    update users set user_name = @UserName, display_name = @DisplayName, role = @Role,
                        employee_id = @EmployeeId, is_active = @IsActive
                    where id = @Id
                    """, new { form.Id, UserName = form.UserName.Trim(), DisplayName = form.DisplayName.Trim(), form.Role, form.EmployeeId, form.IsActive });
            }

            await audit.WriteAsync(conn, form.Id is null ? "Add user" : "Edit user", "User", form.Id, $"{form.UserName} ({form.Role}){(form.IsActive ? "" : " inactive")}");
            return Result.Success();
        }
    }
}

public static class ResetPassword
{
    public sealed class Handler(Db db, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(int userId, string newPassword)
        {
            if (!PasswordRules.IsValid(newPassword, out var why)) return Result.Fail(why);
            var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), newPassword);

            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync("update users set password_hash = @hash where id = @userId", new { hash, userId });
            await audit.WriteAsync(conn, "Reset password", "User", userId);
            return Result.Success();
        }
    }
}

public static class PasswordRules
{
    public static bool IsValid(string? password, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(password) || password.Length < 8) error = "Password must be at least 8 characters.";
        else if (!password.Any(char.IsDigit) || !password.Any(char.IsLetter)) error = "Password must contain letters and numbers.";
        return error.Length == 0;
    }
}

// -----------------------------------------------------------------------------
public static class GetAuditLog
{
    public sealed class Entry
    {
        public DateTime At { get; set; }
        public string UserName { get; set; } = "";
        public string Action { get; set; } = "";
        public string Entity { get; set; } = "";
        public string? Details { get; set; }
    }

    public sealed class Handler(Db db, CurrentStore store)
    {
        public async Task<List<Entry>> HandleAsync(string? search, string? entity)
        {
            await using var conn = await db.OpenAsync();
            var rows = await conn.QueryAsync<Entry>("""
                select at, user_name, action, entity, details from audit_log
                where store_id = @StoreId
                  and (@Entity::text is null or entity = @Entity)
                  and (@Search::text is null or user_name ilike '%' || @Search || '%'
                       or action ilike '%' || @Search || '%' or details ilike '%' || @Search || '%')
                order by at desc
                limit 300
                """, new { StoreId = store.Id, Entity = string.IsNullOrEmpty(entity) ? null : entity, Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() });
            return rows.ToList();
        }
    }
}
