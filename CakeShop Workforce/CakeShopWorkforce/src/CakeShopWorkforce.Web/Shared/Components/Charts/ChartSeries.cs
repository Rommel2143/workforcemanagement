namespace CakeShopWorkforce.Web.Shared.Components.Charts;

/// <summary>One data series for a chart: a name, a color and one value per label.</summary>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values);

/// <summary>Palette that matches the Teams-style theme.</summary>
public static class ChartColors
{
    public const string Brand = "#5B5FC7";
    public const string BrandLight = "#B6B9F0";
    public const string Teal = "#00A3A3";
    public const string Green = "#13A10E";
    public const string Orange = "#CA5010";
    public const string Red = "#C4314B";
    public const string Grey = "#A8A8A8";
}
