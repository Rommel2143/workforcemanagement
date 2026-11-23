using System.Globalization;
using CakeShopWorkforce.Web.Features.Analytics;
using CakeShopWorkforce.Web.Shared;
using Dapper;

namespace CakeShopWorkforce.Web.Features.Sales;

// =============================================================================
//  SALES FEATURE — daily totals and the optional hourly breakdown from the POS.
//     GetMonthSales   → one row per day of the month
//     GetDaySales     → one day, with its hourly rows (for the edit panel)
//     SaveDaySales    → save a day. If hourly rows are given, the daily total = their sum.
//     DeleteDaySales  → remove a day (daily + hourly)
//     ImportSalesCsv  → read "date,hour,transactions,sales" text, preview, then save
// =============================================================================

public sealed class SalesDay
{
    public DateTime BusinessDate { get; set; }
    public int Transactions { get; set; }
    public decimal Sales { get; set; }
    public int HourCount { get; set; }
    public bool IsDemo { get; set; }

    public decimal? AverageTransactionValue => Transactions == 0 ? null : Math.Round(Sales / Transactions, 2);
}

public static class GetMonthSales
{
    public sealed class Handler(Db db, CurrentStore store)
    {
        public async Task<List<SalesDay>> HandleAsync(DateTime anyDate)
        {
            var from = Dates.StartOfMonth(anyDate);
            await using var conn = await db.OpenAsync();
            var rows = await conn.QueryAsync<SalesDay>("""
                select d.business_date, d.transactions, d.sales, d.is_demo,
                       (select count(*) from sales_hourly h where h.store_id = d.store_id and h.business_date = d.business_date) as hour_count
                from sales_daily d
                where d.store_id = @StoreId and d.business_date between @From::date and @To::date
                order by d.business_date
                """, new { StoreId = store.Id, From = from, To = from.AddMonths(1).AddDays(-1) });
            return rows.ToList();
        }
    }
}

// -----------------------------------------------------------------------------
public static class GetDaySales
{
    public sealed class Result
    {
        public DateTime Date { get; init; }
        public bool Exists { get; init; }
        public int Transactions { get; init; }
        public decimal Sales { get; init; }
        public List<SalesHourRow> Hours { get; init; } = [];
    }

    public sealed class Handler(Db db, CurrentStore store)
    {
        public async Task<Result> HandleAsync(DateTime date)
        {
            await using var conn = await db.OpenAsync();
            var p = new { StoreId = store.Id, Date = date.Date };
            var day = await conn.QuerySingleOrDefaultAsync<SalesDayRow>(
                "select business_date, transactions, sales from sales_daily where store_id = @StoreId and business_date = @Date::date", p);
            var hours = await conn.QueryAsync<SalesHourRow>(
                "select business_date, hour, transactions, sales from sales_hourly where store_id = @StoreId and business_date = @Date::date order by hour", p);

            return new Result
            {
                Date = date.Date,
                Exists = day is not null,
                Transactions = day?.Transactions ?? 0,
                Sales = day?.Sales ?? 0,
                Hours = hours.ToList()
            };
        }
    }
}

// -----------------------------------------------------------------------------
public static class SaveDaySales
{
    public sealed class HourInput
    {
        public int Hour { get; set; }
        public int? Transactions { get; set; }
        public decimal? Sales { get; set; }
    }

    public sealed class Command
    {
        public DateTime Date { get; set; }
        public bool ByHour { get; set; }
        public int Transactions { get; set; }
        public decimal Sales { get; set; }
        public List<HourInput> Hours { get; set; } = [];
    }

    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task<Result> HandleAsync(Command cmd)
        {
            var hours = cmd.ByHour
                ? cmd.Hours.Where(h => (h.Transactions ?? 0) > 0 || (h.Sales ?? 0) > 0).ToList()
                : [];

            if (hours.Any(h => h.Transactions < 0 || h.Sales < 0) || cmd.Transactions < 0 || cmd.Sales < 0)
                return Result.Fail("Numbers cannot be negative.");

            var transactions = cmd.ByHour ? hours.Sum(h => h.Transactions ?? 0) : cmd.Transactions;
            var sales = cmd.ByHour ? hours.Sum(h => h.Sales ?? 0) : cmd.Sales;

            await using var conn = await db.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var p = new { StoreId = store.Id, Date = cmd.Date.Date };

            await conn.ExecuteAsync("delete from sales_hourly where store_id = @StoreId and business_date = @Date::date", p, tx);
            foreach (var h in hours)
            {
                await conn.ExecuteAsync("""
                    insert into sales_hourly (store_id, business_date, hour, transactions, sales)
                    values (@StoreId, @Date::date, @Hour, @Transactions, @Sales)
                    """, new { p.StoreId, p.Date, h.Hour, Transactions = h.Transactions ?? 0, Sales = h.Sales ?? 0 }, tx);
            }

            await conn.ExecuteAsync("""
                insert into sales_daily (store_id, business_date, transactions, sales, updated_at)
                values (@StoreId, @Date::date, @transactions, @sales, now())
                on conflict (store_id, business_date) do update set
                    transactions = excluded.transactions, sales = excluded.sales, is_demo = false, updated_at = now()
                """, new { p.StoreId, p.Date, transactions, sales }, tx);

            await tx.CommitAsync();
            await audit.WriteAsync(conn, "Save sales", "Sales", Format.DateIso(cmd.Date), $"{transactions} transactions, {Format.Money(sales)}");
            return Result.Success();
        }
    }
}

// -----------------------------------------------------------------------------
public static class DeleteDaySales
{
    public sealed class Handler(Db db, CurrentStore store, AuditLogger audit)
    {
        public async Task HandleAsync(DateTime date)
        {
            await using var conn = await db.OpenAsync();
            var p = new { StoreId = store.Id, Date = date.Date };
            await conn.ExecuteAsync("delete from sales_hourly where store_id = @StoreId and business_date = @Date::date", p);
            await conn.ExecuteAsync("delete from sales_daily where store_id = @StoreId and business_date = @Date::date", p);
            await audit.WriteAsync(conn, "Delete sales", "Sales", Format.DateIso(date));
        }
    }
}

// -----------------------------------------------------------------------------
public static class ImportSalesCsv
{
    public sealed record Line(int LineNumber, DateTime Date, int? Hour, int Transactions, decimal Sales);

    public sealed class Preview
    {
        public List<Line> Lines { get; } = [];
        public List<string> Errors { get; } = [];
        public int Days => Lines.Select(l => l.Date).Distinct().Count();
        public bool CanImport => Lines.Count > 0 && Errors.Count == 0;
    }

    /// <summary>
    /// Reads CSV text. Columns: date,hour,transactions,sales
    ///   • date  — 2026-11-23 (also accepts 11/23/2026)
    ///   • hour  — 0–23, or empty for a whole-day total
    /// A header row is skipped automatically.
    /// </summary>
    public static Preview Parse(string text)
    {
        var preview = new Preview();
        var lines = text.Replace("\r", "").Split('\n');
        string[] dateFormats = ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "yyyy/MM/dd"];

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (raw.Length == 0) continue;
            var cols = raw.Split(',').Select(c => c.Trim().Trim('"')).ToArray();

            if (i == 0 && cols[0].Equals("date", StringComparison.OrdinalIgnoreCase)) continue;   // header
            var n = i + 1;

            if (cols.Length < 4) { preview.Errors.Add($"Line {n}: expected 4 columns (date,hour,transactions,sales)."); continue; }

            if (!DateTime.TryParseExact(cols[0], dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { preview.Errors.Add($"Line {n}: \"{cols[0]}\" is not a date (use 2026-11-23)."); continue; }

            int? hour = null;
            if (cols[1].Length > 0)
            {
                if (!int.TryParse(cols[1], out var h) || h is < 0 or > 23) { preview.Errors.Add($"Line {n}: hour must be 0–23 or empty."); continue; }
                hour = h;
            }

            if (!int.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tx) || tx < 0)
            { preview.Errors.Add($"Line {n}: transactions must be a whole number."); continue; }

            if (!decimal.TryParse(cols[3].Replace("₱", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var sales) || sales < 0)
            { preview.Errors.Add($"Line {n}: sales must be a number (no thousands separator commas)."); continue; }

            preview.Lines.Add(new Line(n, date.Date, hour, tx, sales));
        }

        foreach (var dup in preview.Lines.GroupBy(l => (l.Date, l.Hour)).Where(g => g.Count() > 1))
            preview.Errors.Add($"{dup.Key.Date:yyyy-MM-dd} {(dup.Key.Hour is null ? "daily total" : "hour " + dup.Key.Hour)} appears {dup.Count()} times.");

        if (preview.Lines.Count == 0 && preview.Errors.Count == 0)
            preview.Errors.Add("The file has no data rows.");

        return preview;
    }

    public sealed class Handler(SaveDaySales.Handler save)
    {
        /// <summary>Saves each day. Days with hourly rows: daily total = sum of hours.</summary>
        public async Task<int> HandleAsync(Preview preview)
        {
            var days = 0;
            foreach (var group in preview.Lines.GroupBy(l => l.Date))
            {
                var hourly = group.Where(l => l.Hour is not null).ToList();
                var daily = group.FirstOrDefault(l => l.Hour is null);

                var cmd = new SaveDaySales.Command
                {
                    Date = group.Key,
                    ByHour = hourly.Count > 0,
                    Transactions = daily?.Transactions ?? 0,
                    Sales = daily?.Sales ?? 0,
                    Hours = hourly.Select(h => new SaveDaySales.HourInput { Hour = h.Hour!.Value, Transactions = h.Transactions, Sales = h.Sales }).ToList()
                };
                var result = await save.HandleAsync(cmd);
                if (result.Ok) days++;
            }
            return days;
        }
    }
}
