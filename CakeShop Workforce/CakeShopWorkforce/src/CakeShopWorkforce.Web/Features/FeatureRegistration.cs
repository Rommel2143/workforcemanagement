using CakeShopWorkforce.Web.Features.Alerts;
using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Features.Attendance;
using CakeShopWorkforce.Web.Features.Auth;
using CakeShopWorkforce.Web.Features.Dashboard;
using CakeShopWorkforce.Web.Features.Employees;
using CakeShopWorkforce.Web.Features.MySchedule;
using CakeShopWorkforce.Web.Features.Reports;
using CakeShopWorkforce.Web.Features.Sales;
using CakeShopWorkforce.Web.Features.Schedule;
using CakeShopWorkforce.Web.Features.Settings;

namespace CakeShopWorkforce.Web.Features;

/// <summary>
/// Registers every feature handler with dependency injection, so pages can write
/// <c>@inject GetEmployees.Handler Handler</c>.
///
/// NO MAGIC: each handler is listed by hand. When you add a new slice, add one line here.
/// (If you forget, the page shows an error saying it "cannot provide a value" for the handler.)
///
/// "Scoped" = one instance per signed-in browser connection (Blazor circuit) or per HTTP request.
/// </summary>
public static class FeatureRegistration
{
    public static IServiceCollection AddFeatures(this IServiceCollection services)
    {
        // Shared calculation engine
        services.AddScoped<DailyFactsLoader>();

        // Auth
        services.AddScoped<ValidateLogin.Handler>();

        // Dashboard & analytics
        services.AddScoped<GetDashboard.Handler>();
        services.AddScoped<GetManpowerAnalysis.Handler>();
        services.AddScoped<GetAlerts.Handler>();

        // Employees
        services.AddScoped<GetEmployees.Handler>();
        services.AddScoped<SaveEmployee.Handler>();
        services.AddScoped<SetEmployeeActive.Handler>();

        // Schedule
        services.AddScoped<GetWeekSchedule.Handler>();
        services.AddScoped<GetMonthSchedule.Handler>();
        services.AddScoped<SaveAssignment.Handler>();
        services.AddScoped<ClearAssignment.Handler>();
        services.AddScoped<CopyWeek.Handler>();
        services.AddScoped<FillRestDays.Handler>();
        services.AddScoped<PublishWeek.Handler>();
        services.AddScoped<GetMySchedule.Handler>();

        // Attendance
        services.AddScoped<GetDailyAttendance.Handler>();
        services.AddScoped<SaveAttendance.Handler>();
        services.AddScoped<FillScheduledTimes.Handler>();

        // Sales
        services.AddScoped<GetMonthSales.Handler>();
        services.AddScoped<GetDaySales.Handler>();
        services.AddScoped<SaveDaySales.Handler>();
        services.AddScoped<DeleteDaySales.Handler>();
        services.AddScoped<ImportSalesCsv.Handler>();

        // Reports
        services.AddScoped<BuildReport.Handler>();

        // Settings
        services.AddScoped<SaveStore.Handler>();
        services.AddScoped<SaveStation.Handler>();
        services.AddScoped<DeleteStation.Handler>();
        services.AddScoped<SaveShiftTemplate.Handler>();
        services.AddScoped<GetUsers.Handler>();
        services.AddScoped<SaveUser.Handler>();
        services.AddScoped<ResetPassword.Handler>();
        services.AddScoped<GetAuditLog.Handler>();
        services.AddScoped<GetDemoStatus.Handler>();
        services.AddScoped<LoadDemoData.Handler>();
        services.AddScoped<RemoveDemoData.Handler>();

        return services;
    }
}
