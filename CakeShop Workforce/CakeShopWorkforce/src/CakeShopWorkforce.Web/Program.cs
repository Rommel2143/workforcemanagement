// =============================================================================
//  CakeShop Workforce — application start-up
//
//  Read this file top to bottom. It does four things:
//    1. Registers services (database, login, our features)
//    2. Builds the app
//    3. Sets up the database (creates tables on first run)
//    4. Maps pages and endpoints, then runs
// =============================================================================

using CakeShopWorkforce.Web.Components;
using CakeShopWorkforce.Web.Database;
using CakeShopWorkforce.Web.Features;
using CakeShopWorkforce.Web.Features.Auth;
using CakeShopWorkforce.Web.Features.Reports;
using CakeShopWorkforce.Web.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ---- 1. Services ------------------------------------------------------------

// Blazor with interactive server components (UI updates live over a SignalR connection)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database: one shared connection pool for the whole app
var connectionString = builder.Configuration.GetConnectionString("CakeShop")
    ?? throw new InvalidOperationException("Add ConnectionStrings:CakeShop to appsettings.json");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// Dapper: map snake_case columns (employee_code) to PascalCase properties (EmployeeCode)
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// Login with a cookie. Unknown users are sent to /login.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Shared helpers used by every feature
builder.Services.AddSingleton<Clock>();
builder.Services.AddSingleton<CurrentStore>();
builder.Services.AddScoped<Db>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<StoreConfig>();
builder.Services.AddScoped<ToastService>();

// Every feature handler (see Features/FeatureRegistration.cs)
builder.Services.AddFeatures();

// ---- 2. Build ---------------------------------------------------------------

var app = builder.Build();

// ---- 3. Database setup (tables + first login) -------------------------------

await DatabaseMigrator.MigrateAsync(app.Services, app.Configuration, app.Logger);

// ---- 4. Pipeline ------------------------------------------------------------

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Plain HTTP endpoints (not pages): sign-out and Excel downloads
app.MapAuthEndpoints();
app.MapReportEndpoints();

app.Run();
