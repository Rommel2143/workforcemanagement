using CakeShopWorkforce.Web.Shared;
using CakeShopWorkforce.Web.Shared.BusinessRules;

namespace CakeShopWorkforce.Web.Features.Analytics;

/// <summary>
/// "How many COUNTER people (Cashier + Sales) do we need during this clock hour?" (spec 9.4, hour-aware part).
///
///   Required = MAX( sum of the counter stations' minimum headcount,
///                   CEILING(transactions this hour ÷ target transactions per person-hour) )
///
/// Only transaction-driven stations are counted. Bakers and decorators are needed for
/// production, not for walk-in traffic, so they are planned with their station minimum
/// (see the Stations table) and never compared against hourly customer demand.
/// </summary>
public static class HourlyDemand
{
    public static int Required(StoreSetup setup, double transactionsThisHour)
    {
        var counterMinimum = setup.Stations.Where(s => s.IsTransactionDriven).Sum(s => s.MinHeadcount);
        var target = Math.Max(1, setup.Store.TargetTxPerPersonHour);
        var needed = (int)Math.Ceiling(transactionsThisHour / target);
        return Math.Max(counterMinimum, needed);
    }

    /// <summary>Coverage ratio = counter people working ÷ counter people required (1.0 = exactly enough).</summary>
    public static decimal? Coverage(int working, int required) =>
        required == 0 ? null : Math.Round((decimal)working / required, 2);

    /// <summary>
    /// Suggests how many of each shift template to give COUNTER staff so every operating
    /// hour has enough people. Greedy and explainable: keep adding the shift that covers
    /// the most still-missing hours until nothing is missing.
    /// </summary>
    public static List<(ShiftTemplate Template, int Count)> SuggestShifts(StoreSetup setup, IReadOnlyList<int> requiredByHour)
    {
        var templates = setup.ShiftTemplates.Where(t => t.IsActive).ToList();
        var counts = templates.ToDictionary(t => t.Id, _ => 0);
        if (templates.Count == 0) return [];

        var hours = setup.Store.OperatingHours().ToList();
        var covered = new int[24];

        for (var guard = 0; guard < 100; guard++)
        {
            var missing = hours.Where(h => covered[h] < requiredByHour[h]).ToList();
            if (missing.Count == 0) break;

            // The shift that fixes the most missing hours (ties → the shorter shift)
            var best = templates
                .Select(t => (Template: t, Fixes: missing.Count(h => ShiftMath.CoversHour(t.StartTime, t.EndTime, h))))
                .Where(x => x.Fixes > 0)
                .OrderByDescending(x => x.Fixes).ThenBy(x => x.Template.PaidHours)
                .FirstOrDefault();
            if (best.Template is null) break;   // no template covers the missing hours

            counts[best.Template.Id]++;
            for (var h = 0; h < 24; h++)
                if (ShiftMath.CoversHour(best.Template.StartTime, best.Template.EndTime, h)) covered[h]++;
        }

        return templates.Select(t => (t, counts[t.Id])).ToList();
    }
}
