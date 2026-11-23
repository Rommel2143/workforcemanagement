using System.Security.Claims;
using CakeShopWorkforce.Web.Shared;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace CakeShopWorkforce.Web.Features.Auth;

/// <summary>A row from the users table.</summary>
public sealed class AppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Turns the user into the "claims" stored inside the login cookie.</summary>
    public ClaimsPrincipal ToPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Id.ToString()),
            new(ClaimTypes.Name, UserName),
            new(ClaimTypes.GivenName, DisplayName),
            new(ClaimTypes.Role, Role)
        };
        if (EmployeeId is not null) claims.Add(new("employee_id", EmployeeId.Value.ToString()));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name ?? "User";

    public static string RoleName(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Role) ?? "";

    public static int? EmployeeId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("employee_id"), out var id) ? id : null;
}

/// <summary>SLICE: check a user name + password. Returns the user, or null when wrong.</summary>
public static class ValidateLogin
{
    public sealed class Handler(Db db)
    {
        public async Task<AppUser?> HandleAsync(string userName, string password)
        {
            await using var conn = await db.OpenAsync();
            var user = await conn.QuerySingleOrDefaultAsync<AppUser>(
                "select * from users where lower(user_name) = lower(@userName) and is_active",
                new { userName = userName.Trim() });

            if (user is null) return null;

            var check = new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, password);
            return check == PasswordVerificationResult.Failed ? null : user;
        }
    }
}

/// <summary>HTTP endpoints for auth that are not pages.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // The "Sign out" button in the top bar posts here
        app.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }
}
