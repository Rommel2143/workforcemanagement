using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Components.Authorization;
using Npgsql;

namespace CakeShopWorkforce.Web.Shared;

/// <summary>
/// Which store are we working with? This app manages ONE store, but every
/// table has a store_id so a second branch can be added later without
/// redesigning the database. The id comes from appsettings.json → "Store:Id".
/// </summary>
public sealed class CurrentStore(IConfiguration config)
{
    public int Id => config.GetValue("Store:Id", 1);
}

/// <summary>
/// The store's local clock. Servers often run in UTC, but "today" for a
/// cake shop in Manila must be Manila's today. Always use Clock.Today /
/// Clock.Now instead of DateTime.Now.
/// </summary>
public sealed class Clock
{
    private readonly TimeZoneInfo _timeZone;

    public Clock(IConfiguration config)
    {
        var id = config["Store:TimeZone"] ?? "Asia/Manila";
        try { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { _timeZone = TimeZoneInfo.Local; }
    }

    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone);
    public DateTime Today => Now.Date;
}

/// <summary>Who is signed in? Used by pages (role checks) and by the audit log.</summary>
public sealed class CurrentUser(AuthenticationStateProvider authState)
{
    public async Task<ClaimsPrincipal> GetAsync() => (await authState.GetAuthenticationStateAsync()).User;

    public async Task<string> GetNameAsync() => (await GetAsync()).Identity?.Name ?? "system";

    public async Task<bool> IsInAnyRoleAsync(params string[] roles)
    {
        var user = await GetAsync();
        return roles.Any(user.IsInRole);
    }
}

/// <summary>
/// Writes one row to the audit_log table. Call it whenever data changes,
/// especially schedules (the spec requires an audit trail for schedule changes).
/// </summary>
public sealed class AuditLogger(CurrentUser user, CurrentStore store)
{
    public async Task WriteAsync(NpgsqlConnection conn, string action, string entity, object? entityId, string? details = null)
    {
        var userName = await user.GetNameAsync();
        await conn.ExecuteAsync("""
            insert into audit_log (store_id, user_name, action, entity, entity_id, details)
            values (@StoreId, @UserName, @Action, @Entity, @EntityId, @Details)
            """,
            new { StoreId = store.Id, UserName = userName, Action = action, Entity = entity, EntityId = entityId?.ToString(), Details = details });
    }
}

/// <summary>
/// The answer from a command (save, delete...). Either it worked, or it
/// failed with a message we can show the user.
/// </summary>
public sealed record Result(bool Ok, string? Error = null)
{
    public static Result Success() => new(true);
    public static Result Fail(string error) => new(false, error);
}

/// <summary>The four sign-in roles, written once so nobody misspells them.</summary>
public static class Roles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Supervisor = "Supervisor";
    public const string Staff = "Staff";

    // Handy combinations for [Authorize(Roles = ...)]
    public const string Managers = "Owner,Manager";
    public const string Leaders = "Owner,Manager,Supervisor";

    public static readonly string[] All = [Owner, Manager, Supervisor, Staff];
}
