# HANDOFF — status

**All 15 items from the original handoff are done.** The app builds with no errors or warnings, runs against PostgreSQL 18, and every page was clicked through with demo data loaded. See `README.md` for setup and the folder map.

Full requirements and design: `../CakeShop_Personnel_System_Strategic_Plan.md`.

## Decisions (unchanged, plus two made while building)

| Topic | Decision |
|---|---|
| Stack | .NET 10 Blazor Web App (Interactive Server, `prerender: false`), PostgreSQL, Dapper, Npgsql, ClosedXML |
| Architecture | Vertical slices in `Features/<Feature>/`; handlers registered by hand in `Features/FeatureRegistration.cs` |
| Calculation engine | `Features/Analytics/DailyFacts.cs` (`DailyFactsLoader`) is the single place that turns schedule + attendance + sales into numbers. Dashboard, Analytics, Alerts, Reports and My shifts all use it |
| **Hourly coverage = counter staff only** | Hourly demand compares only Cashier + Sales people on duty (`DayFacts.CounterPresentByHour`) with `CEILING(tx ÷ target per person-hour)`. Bakers and decorators are planned with their station minimum, not hourly traffic. Otherwise, closing hours always looked "short" because production goes home at 3 PM |
| **TPLH benchmark = store's own median** | TPLH divides by ALL labor hours, so the counter-only "target per person-hour" setting doesn't fit it. Analytics, the A08 alert and the productivity report compare days against the period's median TPLH (plan §12.3, "suggested benchmark") |
| Excel | `GET /reports/{type}.xlsx?from=&to=` (Owner/Manager only). PDF = `window.print()` + print CSS |
| Demo data | `Settings → Demo data`. Rows flagged `is_demo`; banner shown while present; remove deletes exactly those rows |

## Verified in the running app

- First run creates the DB + tables + default Owner; sign-in / sign-out; Staff login lands on My shifts and gets "No access" on manager pages
- Dashboard (daily/weekly/monthly), alerts bell, demo banner
- Schedule: week grid, drag-and-drop (Blazor drop handler verified with dispatched drag events), side-panel edit, rest day, footer headcount vs forecast ideal, station rows, warnings, month view
- Attendance: inline edit, "Now", undo absent, live status/summary recalculation
- People: search via the top-bar `?q=`, list
- Sales: month list, CSV preview with line-level errors
- Analytics: KPIs, 8 questions, recommendations, charts, daily table, heatmap, hour table, shift suggestions
- Reports: preview; all 9 Excel downloads return valid `.xlsx` (unknown type → 404)
- Settings: store, stations, templates, users (add / disable), audit log, demo load / remove

## Not verified yet

- Print / Save as PDF output (print CSS written, dialog not opened)
- Clicking the Excel link in the page (endpoint verified with `fetch`)
- Phone layout (< 768 px, bottom rail) and drag-and-drop in Firefox (`wwwroot/app.js` sets drag data for it)

## Not built (later phases in the plan)

Leave requests and approvals, timecard approval, biometric / CSV attendance import, holiday and event calendar (forecast multipliers), effective-dated benchmarks, per-station hourly coverage heatmap, multi-store picker, email alerts.

## Local test data

- A Staff test login `kim.test` was created to check the staff view. It is **disabled**; delete it from the `users` table if you don't want it.
- Demo data is currently loaded in the local `cakeshop_workforce` database.
