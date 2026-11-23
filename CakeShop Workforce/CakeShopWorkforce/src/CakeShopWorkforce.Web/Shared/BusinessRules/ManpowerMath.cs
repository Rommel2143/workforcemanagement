namespace CakeShopWorkforce.Web.Shared.BusinessRules;

public enum StaffingStatus { NoData, Understaffed, Optimal, PotentiallyOverstaffed }

/// <summary>How many people one station needed vs. how many were there.</summary>
public sealed class StationRequirement
{
    public required Station Station { get; init; }
    public int Required { get; init; }
    public int Present { get; init; }
    public int Gap => Present - Required;           // negative = short
}

/// <summary>Every productivity number for one day (or one period).</summary>
public sealed class ManpowerResult
{
    public int Transactions { get; init; }
    public decimal Sales { get; init; }
    public int Present { get; init; }
    public decimal LaborHours { get; init; }

    public decimal? AverageTransactionValue { get; init; }   // Sales / Transactions
    public decimal? TransactionsPerPersonnel { get; init; }  // Transactions / Present
    public decimal? SalesPerPersonnel { get; init; }         // Sales / Present
    public decimal? TransactionsPerLaborHour { get; init; }  // Transactions / Labor hours
    public decimal? SalesPerLaborHour { get; init; }         // Sales / Labor hours

    public int SimpleIdeal { get; init; }                    // CEILING(Transactions / target) – the spec formula
    public int Ideal { get; init; }                          // station-aware (recommended)
    public int Gap => Present - Ideal;                       // negative = shortage, positive = excess
    public decimal? Utilization { get; init; }               // Ideal / Present × 100
    public StaffingStatus Status { get; init; }
    public List<StationRequirement> Stations { get; init; } = [];
}

/// <summary>
/// The manpower formulas from the spec, plus a station-aware "Ideal".
///
/// SPEC FORMULAS
///   Average Transaction Value          = Sales / Transactions
///   Transactions Per Personnel          = Transactions / Personnel Present
///   Sales Per Personnel                 = Sales / Personnel Present
///   Transactions Per Personnel Per Hour = Transactions / Total Labor Hours
///   Ideal Personnel (simple)            = CEILING(Transactions / Target Transactions Per Personnel)
///
/// WHY A SECOND "IDEAL"?
///   The spec also says: "Separate production, decorating, cashiering and sales".
///   Bakers and decorators are needed even on a slow day, so:
///     • Transaction-driven stations (Cashier, Sales): MAX(min headcount, CEILING(Transactions / station target))
///     • Fixed stations (Production, Decorating):     min headcount
///   Ideal = sum of all stations. This is what the status uses.
/// </summary>
public static class ManpowerMath
{
    public static decimal? Divide(decimal top, decimal bottom, int decimals = 2) =>
        bottom == 0 ? null : Math.Round(top / bottom, decimals);

    public static int SimpleIdeal(decimal transactions, int targetPerPersonnel) =>
        targetPerPersonnel <= 0 ? 0 : (int)Math.Ceiling(transactions / targetPerPersonnel);

    public static int RequiredForStation(Station station, decimal transactions)
    {
        if (station.IsTransactionDriven && station.TargetTxPerPerson is > 0)
            return Math.Max(station.MinHeadcount, (int)Math.Ceiling(transactions / station.TargetTxPerPerson.Value));

        return station.MinHeadcount;
    }

    public static int IdealForTransactions(IEnumerable<Station> stations, decimal transactions) =>
        stations.Sum(s => RequiredForStation(s, transactions));

    public static ManpowerResult Analyze(
        int transactions,
        decimal sales,
        bool hasSalesData,
        int present,
        decimal laborHours,
        IReadOnlyDictionary<int, int> presentByStation,
        IReadOnlyList<Station> stations,
        int targetTxPerPersonnel)
    {
        var stationRows = stations.Select(s => new StationRequirement
        {
            Station = s,
            Required = RequiredForStation(s, transactions),
            Present = presentByStation.GetValueOrDefault(s.Id)
        }).ToList();

        var ideal = stationRows.Sum(r => r.Required);

        StaffingStatus status;
        if (!hasSalesData) status = StaffingStatus.NoData;
        else if (present < ideal || stationRows.Any(r => r.Gap < 0)) status = StaffingStatus.Understaffed;
        else if (present == ideal) status = StaffingStatus.Optimal;
        else status = StaffingStatus.PotentiallyOverstaffed;

        return new ManpowerResult
        {
            Transactions = transactions,
            Sales = sales,
            Present = present,
            LaborHours = laborHours,
            AverageTransactionValue = Divide(sales, transactions),
            TransactionsPerPersonnel = Divide(transactions, present, 1),
            SalesPerPersonnel = Divide(sales, present),
            TransactionsPerLaborHour = Divide(transactions, laborHours, 1),
            SalesPerLaborHour = Divide(sales, laborHours),
            SimpleIdeal = SimpleIdeal(transactions, targetTxPerPersonnel),
            Ideal = ideal,
            Utilization = present == 0 ? null : Math.Round(ideal * 100m / present, 0),
            Status = status,
            Stations = stationRows
        };
    }

    public static string Label(StaffingStatus status) => status switch
    {
        StaffingStatus.Understaffed => "Understaffed",
        StaffingStatus.Optimal => "Optimal",
        StaffingStatus.PotentiallyOverstaffed => "Potentially overstaffed",
        _ => "No sales data"
    };

    public static string Tone(StaffingStatus status) => status switch
    {
        StaffingStatus.Understaffed => "danger",
        StaffingStatus.Optimal => "success",
        StaffingStatus.PotentiallyOverstaffed => "warning",
        _ => "neutral"
    };

    /// <summary>Plain-language advice. Never says "cut people" — it suggests moving hours (spec rule).</summary>
    public static string Advice(ManpowerResult r) => r.Status switch
    {
        StaffingStatus.Understaffed => r.Stations.FirstOrDefault(s => s.Gap < 0) is { } short_
            ? $"Short by {-short_.Gap} at {short_.Station.Name}. Add a shift or move someone with that skill."
            : $"Short by {-r.Gap} overall. Consider adding a Mid shift.",
        StaffingStatus.Optimal => "Staffing matched the workload.",
        StaffingStatus.PotentiallyOverstaffed =>
            $"{r.Gap} more than needed. Before reducing anyone, check shift coverage and roles — consider moving hours to busier days.",
        _ => "Enter sales for this day to see the analysis."
    };
}
