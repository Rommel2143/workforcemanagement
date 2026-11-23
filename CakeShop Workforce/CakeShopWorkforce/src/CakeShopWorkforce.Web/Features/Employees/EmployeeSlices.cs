using System.ComponentModel.DataAnnotations;
using CakeShopWorkforce.Web.Shared;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Employees;

// =============================================================================
//  EMPLOYEES FEATURE — all database work for the employee masterlist.
//
//  Each "slice" below is one thing a user can do:
//     GetEmployees      → list / search employees
//     SaveEmployee      → add a new employee or update one
//     SetEmployeeActive → deactivate or re-activate
//
//  Pattern used by every slice in this project:
//     public static class <ActionName>
//     {
//         (input / output classes)
//         public sealed class Handler(...) { HandleAsync(...) }   ← the actual work
//     }
//  The page (EmployeesPage.razor) injects the handler and calls HandleAsync.
// =============================================================================

/// <summary>One employee as shown in the list.</summary>
public sealed class EmployeeRecord
{
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Position { get; set; } = "";
    public string Department { get; set; } = "";
    public int? StationId { get; set; }
    public string? StationName { get; set; }
    public string? StationColor { get; set; }
    public string EmploymentStatus { get; set; } = "";
    public string EmploymentType { get; set; } = "";
    public string? ContactNumber { get; set; }
    public string? Email { get; set; }
    public string AvailableDays { get; set; } = "";
    public int MaxHoursPerWeek { get; set; }
    public string? RestDay { get; set; }
    public bool IsActive { get; set; }
    public bool IsDemo { get; set; }
}

// -----------------------------------------------------------------------------
public static class GetEmployees
{
    public sealed class Filter
    {
        public string? Search { get; set; }
        public int? StationId { get; set; }
        public bool IncludeInactive { get; set; }
    }

    public sealed class Handler(Db db, CurrentStore store)
    {
        public async Task<List<EmployeeRecord>> HandleAsync(Filter filter)
        {
            await using var conn = await db.OpenAsync();
            var rows = await conn.QueryAsync<EmployeeRecord>("""
                select e.*, s.name as station_name, s.color as station_color
                from employees e
                left join stations s on s.id = e.station_id
                where e.store_id = @StoreId
                  and (@IncludeInactive or e.is_active)
                  and (@StationId::int is null or e.station_id = @StationId)
                  and (@Search::text is null
                       or e.full_name     ilike '%' || @Search || '%'
                       or e.employee_code ilike '%' || @Search || '%'
                       or e.position      ilike '%' || @Search || '%')
                order by e.is_active desc, e.full_name
                """,
                new
                {
                    StoreId = store.Id,
                    filter.IncludeInactive,
                    filter.StationId,
                    Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim()
                });
            return rows.ToList();
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveEmployee
{
    /// <summary>The form on the screen. The [Required] etc. rules show red messages automatically.</summary>
    public sealed class Form
    {
        public int? Id { get; set; }

        [Required(ErrorMessage = "Employee ID is required"), StringLength(20)]
        public string EmployeeCode { get; set; } = "";

        [Required(ErrorMessage = "Name is required"), StringLength(120)]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Position is required"), StringLength(80)]
        public string Position { get; set; } = "";

        [Required, StringLength(80)]
        public string Department { get; set; } = "Store Operations";

        [Required(ErrorMessage = "Choose a station")]
        public int? StationId { get; set; }

        [Required] public string EmploymentStatus { get; set; } = "Regular";
        [Required] public string EmploymentType { get; set; } = "Full-time";

        [Phone] public string? ContactNumber { get; set; }
        [EmailAddress] public string? Email { get; set; }

        /// <summary>Checkbox per day, saved as "Mon,Tue,...".</summary>
        public HashSet<string> AvailableDays { get; set; } = [.. Dates.DayCodes];

        [Range(1, 72, ErrorMessage = "Between 1 and 72 hours")]
        public int MaxHoursPerWeek { get; set; } = 48;

        public string? RestDay { get; set; } = "Sun";

        public static Form From(EmployeeRecord e) => new()
        {
            Id = e.Id,
            EmployeeCode = e.EmployeeCode,
            FullName = e.FullName,
            Position = e.Position,
            Department = e.Department,
            StationId = e.StationId,
            EmploymentStatus = e.EmploymentStatus,
            EmploymentType = e.EmploymentType,
            ContactNumber = e.ContactNumber,
            Email = e.Email,
            AvailableDays = e.AvailableDays.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(),
            MaxHoursPerWeek = e.MaxHoursPerWeek,
            RestDay = e.RestDay
        };
    }

    public static readonly string[] Statuses = ["Regular", "Probationary", "Contractual", "Trainee"];
    public static readonly string[] Types = ["Full-time", "Part-time"];

    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Form form)
        {
            if (form.AvailableDays.Count == 0)
                return Result.Fail("Pick at least one available working day.");

            await using var conn = await db.OpenAsync();

            // Employee ID must be unique in the store
            var duplicate = await conn.ExecuteScalarAsync<bool>("""
                select exists(select 1 from employees
                              where store_id = @StoreId and lower(employee_code) = lower(@Code) and id <> coalesce(@Id, 0))
                """, new { StoreId = store.Id, Code = form.EmployeeCode.Trim(), form.Id });
            if (duplicate) return Result.Fail($"Employee ID {form.EmployeeCode} is already used.");

            var p = new
            {
                StoreId = store.Id,
                form.Id,
                EmployeeCode = form.EmployeeCode.Trim(),
                FullName = form.FullName.Trim(),
                Position = form.Position.Trim(),
                Department = form.Department.Trim(),
                form.StationId,
                form.EmploymentStatus,
                form.EmploymentType,
                form.ContactNumber,
                form.Email,
                // keep Mon..Sun order
                AvailableDays = string.Join(',', Dates.DayCodes.Where(form.AvailableDays.Contains)),
                form.MaxHoursPerWeek,
                RestDay = string.IsNullOrEmpty(form.RestDay) ? null : form.RestDay
            };

            if (form.Id is null)
            {
                var id = await conn.ExecuteScalarAsync<int>("""
                    insert into employees (store_id, employee_code, full_name, position, department, station_id,
                        employment_status, employment_type, contact_number, email, available_days, max_hours_per_week, rest_day)
                    values (@StoreId, @EmployeeCode, @FullName, @Position, @Department, @StationId,
                        @EmploymentStatus, @EmploymentType, @ContactNumber, @Email, @AvailableDays, @MaxHoursPerWeek, @RestDay)
                    returning id
                    """, p);
                await audit.WriteAsync(conn, "Add employee", "Employee", id, p.FullName);
            }
            else
            {
                await conn.ExecuteAsync("""
                    update employees set employee_code = @EmployeeCode, full_name = @FullName, position = @Position,
                        department = @Department, station_id = @StationId, employment_status = @EmploymentStatus,
                        employment_type = @EmploymentType, contact_number = @ContactNumber, email = @Email,
                        available_days = @AvailableDays, max_hours_per_week = @MaxHoursPerWeek, rest_day = @RestDay
                    where id = @Id and store_id = @StoreId
                    """, p);
                await audit.WriteAsync(conn, "Edit employee", "Employee", form.Id, p.FullName);
            }

            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class SetEmployeeActive
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit, Clock clock)
    {
        /// <summary>
        /// Deactivating keeps history (attendance, past schedules) but removes
        /// the person from FUTURE schedules so no shift is left with a ghost.
        /// </summary>
        public async Task<Result> HandleAsync(int employeeId, bool active)
        {
            await using var conn = await db.OpenAsync();
            await conn.ExecuteAsync("update employees set is_active = @active where id = @employeeId and store_id = @StoreId",
                new { active, employeeId, StoreId = store.Id });

            var removed = 0;
            if (!active)
            {
                removed = await conn.ExecuteAsync("""
                    delete from schedule_assignments
                    where employee_id = @employeeId and business_date > @Today::date
                    """, new { employeeId, Today = clock.Today });
            }

            await audit.WriteAsync(conn, active ? "Reactivate employee" : "Deactivate employee", "Employee", employeeId,
                removed > 0 ? $"Removed {removed} future schedule entries" : null);
            return Result.Success();
        }
    }
}
