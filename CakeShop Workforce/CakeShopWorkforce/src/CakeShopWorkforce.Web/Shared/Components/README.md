# Shared UI components

Small building blocks used by every page. Each one is a single `.razor` file.

| Component | What it is | Example |
|---|---|---|
| `Icon` | Outline icon (Teams/Fluent style) | `<Icon Name="calendar" />` |
| `Avatar` | Round initials bubble | `<Avatar Name="Ana Cruz" />` |
| `Badge` | Colored status pill | `<Badge Tone="success">Present</Badge>` |
| `Card` | White panel with a title | `<Card Title="Today">...</Card>` |
| `KpiCard` | Big number tile | `<KpiCard Label="Sales" Value="₱12K" Icon="money" />` |
| `PageHeader` | Page title + buttons | `<PageHeader Title="Employees">...</PageHeader>` |
| `SidePanel` | Slide-in form panel | `<SidePanel Open="open" Title="Add" OnClose="...">` |
| `ConfirmDialog` | "Are you sure?" | `<ConfirmDialog Open="ask" ... />` |
| `EmptyState` | Friendly empty message | `<EmptyState Icon="people" Title="No employees yet" />` |
| `Loading` | Shimmer placeholder | `<Loading Rows="5" />` |
| `ToastHost` | Pop-up notifications (lives in MainLayout) | use `ToastService` |
| `PeriodPicker` | Today / Week / Month / Custom | `<PeriodPicker Value="period" ValueChanged="..." />` |
| `Charts/*` | SVG charts, no JavaScript | `<BarLineChart ... />` |

Colors used by `Tone`: `brand`, `success`, `warning`, `danger`, `info`, `neutral`.
