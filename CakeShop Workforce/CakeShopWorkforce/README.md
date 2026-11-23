# CakeShop Workforce

Schedule planner and personnel analytics for one cake shop. It connects three things the store already has:

```
SCHEDULE (who should work)  +  ATTENDANCE (who actually worked)  +  SALES (how busy we were)
                                         │
                                         ▼
     How many people do we need, at which station, at what hour — and are we using them well?
```

Built with **.NET 10 Blazor** (interactive server), **PostgreSQL**, and **Dapper**. There is no UI component library. The screens follow a Microsoft Teams look, built from the project's own small components.

---

## 1. What you need

| Tool | Version | Check with |
|---|---|---|
| .NET SDK | 10.0 or newer | `dotnet --list-sdks` |
| PostgreSQL | 14 or newer (tested on 16 and 18) | pgAdmin, or `psql --version` |

## 2. First run

1. **Set the database password.** Open `src/CakeShopWorkforce.Web/appsettings.json` and edit the connection string:

   ```json
   "ConnectionStrings": {
     "CakeShop": "Host=localhost;Port=5432;Database=cakeshop_workforce;Username=postgres;Password=YOUR_PASSWORD"
   }
   ```

   You do **not** need to create the database. The app creates it, and all its tables, the first time it starts.

2. **Run the app:**

   ```powershell
   cd src/CakeShopWorkforce.Web
   dotnet run
   ```

   Then open the address it prints (for example `https://localhost:7215`).

3. **Sign in** with the first Owner login from `appsettings.json → DefaultAdmin` (user `admin`). Then **change that password** in *Settings → Users*. The default is only for the first sign-in.

4. **Optional: try it with sample data.** Go to *Settings → Demo data → Load demo data*. You get 14 employees, 8 weeks of schedules, attendance, and hourly sales. A yellow banner stays on screen while sample data exists. *Remove demo data* deletes exactly those rows.

## 3. Using it day to day

| Who | Does what | Where |
|---|---|---|
| Owner / Manager | Add employees, set their station, available days and rest day | **People** |
| Owner / Manager | Build the week: drag shifts onto the grid, fix the warnings, **Publish** | **Schedule** |
| Supervisor | Record time in / time out, mark absences | **Attendance** |
| Owner / Manager | Enter or import (CSV) daily or hourly sales from the POS | **Sales** |
| Owner / Manager | Check staffing vs workload, peak hours, suggestions | **Dashboard**, **Analytics** |
| Owner / Manager | Download Excel or print / save as PDF | **Reports** |
| Staff | See their own shifts and attendance | **My shifts** |

**Business rules built in:**

- Rest days and approved leave are **never** counted as absences.
- Overtime is any work beyond 8 hours a day; the limit can be changed in Settings.
- A 7th consecutive work day is flagged before publishing.
- The app never recommends cutting people. It suggests moving hours to where they are needed.

## 4. Folder map

```
src/CakeShopWorkforce.Web/
├── Program.cs                  ← start here: services, database setup, pages
├── appsettings.json            ← connection string, default login, time zone
├── Database/
│   ├── DatabaseMigrator.cs     ← creates the DB and runs the scripts once
│   └── Scripts/*.sql           ← 001_CreateTables, 002_DefaultSetup (add 003_... to change the DB)
├── Shared/                     ← used by every feature
│   ├── Db.cs, AppContext.cs    ← connection, current user/store, clock (Manila time), audit log, roles
│   ├── Format.cs, Period.cs    ← how money, dates and times look; date ranges
│   ├── StoreSetup.cs           ← store settings, stations, shift templates
│   ├── BusinessRules/          ← the formulas (no database, no UI — easy to read and test)
│   │   ├── ShiftMath.cs        ← paid hours, shifts across midnight
│   │   ├── AttendanceMath.cs   ← present / late / absent / leave / rest day, overtime
│   │   ├── ManpowerMath.cs     ← ATV, TPP, SPP, TPLH, SPLH, ideal manpower, staffing status
│   │   └── ScheduleRules.cs    ← 7-day streak, weekly max, availability, clopening, coverage
│   └── Components/             ← Icon, Avatar, Badge, Card, KpiCard, SidePanel, charts...
├── Components/                 ← App.razor, Routes.razor, layout (title bar, left rail)
├── Features/                   ← ONE FOLDER PER FEATURE (vertical slices)
│   ├── Analytics/DailyFacts.cs ← the shared calculation engine (read this one!)
│   ├── Dashboard/  Schedule/  Attendance/  Employees/  Sales/
│   ├── Analytics/  Alerts/  Reports/  Settings/  MySchedule/  Auth/
│   └── FeatureRegistration.cs  ← every handler is listed here by hand
└── wwwroot/app.css             ← the whole look, in one commented file
```

### How the numbers are made

Pages only **save raw facts**: shifts, time in and out, and sales. `Features/Analytics/DailyFacts.cs` reads those facts for a date range and computes one `DayFacts` per date. That covers headcounts, worked hours, overtime, people per hour, and the manpower analysis. The Dashboard, Analytics, Alerts, Reports and My shifts pages all use it, so every screen shows the same numbers.

| Number | Formula |
|---|---|
| Average transaction value | Sales ÷ Transactions |
| Transactions per personnel (TPP) | Transactions ÷ People present |
| Transactions per labor hour (TPLH) | Transactions ÷ Worked hours (all staff) |
| Ideal personnel — simple | CEILING(Transactions ÷ Target per personnel) |
| Ideal personnel — station-aware (used for status) | Fixed stations at their minimum + Cashier/Sales at MAX(minimum, CEILING(Transactions ÷ station target)) |
| Hourly coverage | Counter staff on duty ÷ CEILING(Transactions that hour ÷ Target per person-hour) |

"—" means the number can't be calculated, for example when there are no sales or nobody was present. The app never shows 0 or "infinity" in those cases.

## 5. How to add a new feature (walkthrough)

Example: a "Notes" page where supervisors leave a note for the day.

1. **Database.** Add `Database/Scripts/003_AddNotes.sql`:
   ```sql
   create table day_notes (
       id serial primary key,
       store_id int not null references stores(id),
       business_date date not null,
       text text not null,
       created_by text not null,
       created_at timestamptz not null default now()
   );
   ```
   Never edit a script that has already run. Always add a new one. It runs automatically on the next start.

2. **Slice.** Create `Features/Notes/NoteSlices.cs`, with one static class per action:
   ```csharp
   namespace CakeShopWorkforce.Web.Features.Notes;

   public static class SaveNote
   {
       public sealed class Handler(Db db, CurrentStore store, CurrentUser user, AuditLogger audit)
       {
           public async Task<Result> HandleAsync(DateTime date, string text)
           {
               if (string.IsNullOrWhiteSpace(text)) return Result.Fail("Write something first.");
               await using var conn = await db.OpenAsync();
               await conn.ExecuteAsync("""
                   insert into day_notes (store_id, business_date, text, created_by)
                   values (@StoreId, @Date::date, @Text, @User)
                   """, new { StoreId = store.Id, Date = date, Text = text.Trim(), User = await user.GetNameAsync() });
               await audit.WriteAsync(conn, "Add note", "Notes", null, text);
               return Result.Success();
           }
       }
   }
   ```
   Tips: column names are `snake_case` in SQL and `PascalCase` in C# (Dapper maps them). Cast date and time parameters in SQL (`@Date::date`, `@Start::time`).

3. **Register it.** Add one line to `Features/FeatureRegistration.cs`:
   ```csharp
   services.AddScoped<SaveNote.Handler>();
   ```

4. **Page.** Create `Features/Notes/NotesPage.razor`:
   ```razor
   @page "/notes"
   @attribute [Authorize(Roles = Roles.Leaders)]
   @inject SaveNote.Handler Save
   @inject ToastService Toasts
   @inject Clock Clock

   <PageHeader Title="Notes" Icon="edit" />
   <Card>
       <textarea class="input" rows="4" @bind="text"></textarea>
       <button class="btn btn-primary" @onclick="SaveAsync">Save</button>
   </Card>

   @code {
       private string text = "";
       private async Task SaveAsync()
       {
           var result = await Save.HandleAsync(Clock.Today, text);
           if (result.Ok) Toasts.Success("Saved"); else Toasts.Error(result.Error!);
       }
   }
   ```

5. **Menu.** Add `<RailLink Href="notes" Icon="edit" Text="Notes" />` to `Components/Layout/NavRail.razor`.

## 6. Roles

| | Owner | Manager | Supervisor | Staff |
|---|:-:|:-:|:-:|:-:|
| Dashboard, Schedule (view), Attendance, People (view), Sales (view), Alerts | ✓ | ✓ | ✓ | – |
| Edit people, build & publish schedule, enter sales | ✓ | ✓ | – | – |
| Analytics, Reports, Settings | ✓ | ✓ | – | – |
| Logins (Settings → Users) | ✓ | – | – | – |
| My shifts | ✓ | ✓ | ✓ | ✓ |

## 7. Troubleshooting

| Problem | Fix |
|---|---|
| `password authentication failed for user "postgres"` | Wrong password in the connection string. |
| `Failed to connect to 127.0.0.1:5432` | PostgreSQL isn't running. Start the service (Windows: *Services → postgresql-x64-NN*). |
| Page says "cannot provide a value for property … Handler" | A new handler is missing from `FeatureRegistration.cs`. |
| Forgot the Owner password | In pgAdmin: `delete from users;` then restart the app. It recreates the default Owner from `appsettings.json`. |
