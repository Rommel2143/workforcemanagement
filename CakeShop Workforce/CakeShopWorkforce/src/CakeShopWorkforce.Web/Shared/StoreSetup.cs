using CakeShopWorkforce.Web.Shared.BusinessRules;
using Dapper;

namespace CakeShopWorkforce.Web.Shared;

// ---------------------------------------------------------------------------
// The store's configuration: settings, stations and shift templates.
// Almost every feature needs these, so they live in Shared.
// The Settings feature is the only place that CHANGES them.
// ---------------------------------------------------------------------------

public sealed class StoreSettings
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public TimeSpan OpenTime { get; set; }
    public TimeSpan CloseTime { get; set; }
    public int GraceMinutes { get; set; }
    public decimal RegularHoursPerDay { get; set; }
    public int TargetTxPerPersonnel { get; set; }
    public int TargetTxPerPersonHour { get; set; }
    public decimal WeeklyOtAlertHours { get; set; }

    /// <summary>Hours the store is open, e.g. 7 AM–9 PM → 7, 8, ... 20</summary>
    public IEnumerable<int> OperatingHours()
    {
        var open = OpenTime.Hours;
        var close = CloseTime <= OpenTime ? 24 : (int)Math.Ceiling(CloseTime.TotalHours);
        for (var h = open; h < close; h++) yield return h;
    }
}

public sealed class Station
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#5B5FC7";
    public string Driver { get; set; } = "Fixed";          // "Transactions" or "Fixed"
    public int? TargetTxPerPerson { get; set; }
    public int MinHeadcount { get; set; }
    public int SortOrder { get; set; }

    public bool IsTransactionDriven => Driver == "Transactions";
}

public sealed class ShiftTemplate
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int BreakMinutes { get; set; }
    public string Color { get; set; } = "#5B5FC7";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }

    public decimal PaidHours => ShiftMath.PaidHours(StartTime, EndTime, BreakMinutes);
}

/// <summary>Everything about the store's setup, loaded in one call.</summary>
public sealed class StoreSetup
{
    public StoreSettings Store { get; init; } = new();
    public List<Station> Stations { get; init; } = [];
    public List<ShiftTemplate> ShiftTemplates { get; init; } = [];

    public Station? FindStation(int? id) => id is null ? null : Stations.FirstOrDefault(s => s.Id == id);
    public ShiftTemplate? FindTemplate(int? id) => id is null ? null : ShiftTemplates.FirstOrDefault(t => t.Id == id);
}

/// <summary>Loads <see cref="StoreSetup"/> from the database.</summary>
public sealed class StoreConfig(Db db, CurrentStore store)
{
    public async Task<StoreSetup> LoadAsync()
    {
        await using var conn = await db.OpenAsync();
        var p = new { StoreId = store.Id };

        var settings = await conn.QuerySingleAsync<StoreSettings>(
            "select * from stores where id = @StoreId", p);

        var stations = await conn.QueryAsync<Station>(
            "select * from stations where store_id = @StoreId order by sort_order, name", p);

        var templates = await conn.QueryAsync<ShiftTemplate>(
            "select * from shift_templates where store_id = @StoreId order by sort_order, start_time", p);

        return new StoreSetup { Store = settings, Stations = stations.ToList(), ShiftTemplates = templates.ToList() };
    }
}
