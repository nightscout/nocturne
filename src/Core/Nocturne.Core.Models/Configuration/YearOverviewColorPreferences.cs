namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// The year overview's color scale for each metric: a focus window, outside which days are
/// dimmed by <see cref="OutOfBandTransparency"/>, and the color boundaries that warp the
/// metric's color ramp.
/// </summary>
public class YearOverviewColorPreferences
{
    internal const int MaxBoundaries = 16;
    private const double GlucoseMin = 40;
    private const double GlucoseMax = 350;

    public double? OutOfBandTransparency { get; set; }

    public double[]? AvgGlucoseBand { get; set; }
    public YearOverviewColorBoundary[]? AvgGlucoseBoundaries { get; set; }
    public double[]? TirBand { get; set; }
    public YearOverviewColorBoundary[]? TirBoundaries { get; set; }
    public double[]? BolusBand { get; set; }
    public YearOverviewColorBoundary[]? BolusBoundaries { get; set; }
    public double[]? BasalBand { get; set; }
    public YearOverviewColorBoundary[]? BasalBoundaries { get; set; }
    public double[]? TddBand { get; set; }
    public YearOverviewColorBoundary[]? TddBoundaries { get; set; }
    public double[]? CarbsBand { get; set; }
    public YearOverviewColorBoundary[]? CarbsBoundaries { get; set; }

    public string? Validate()
    {
        if (OutOfBandTransparency.HasValue && (OutOfBandTransparency.Value < 0 || OutOfBandTransparency.Value > 100))
        {
            return "yearOverviewColors.outOfBandTransparency: invalid_transparency_value";
        }

        foreach (var (metric, band, boundaries, min, max) in new[]
        {
            ("avgGlucose", AvgGlucoseBand, AvgGlucoseBoundaries, GlucoseMin, GlucoseMax),
            ("tir", TirBand, TirBoundaries, 0d, 100d),
            ("bolus", BolusBand, BolusBoundaries, 0d, double.PositiveInfinity),
            ("basal", BasalBand, BasalBoundaries, 0d, double.PositiveInfinity),
            ("tdd", TddBand, TddBoundaries, 0d, double.PositiveInfinity),
            ("carbs", CarbsBand, CarbsBoundaries, 0d, double.PositiveInfinity),
        })
        {
            if (band is not null
                && (band.Length != 2
                    || !band.All(double.IsFinite)
                    || band[0] >= band[1]
                    || band[0] < min
                    || band[1] > max))
            {
                return $"yearOverviewColors.{metric}Band: invalid_color_range";
            }

            if (boundaries is not null
                && (boundaries.Length > MaxBoundaries
                    || boundaries.Any(b => b is null
                        || !double.IsFinite(b.Value) || b.Value <= min || b.Value >= max
                        || !double.IsFinite(b.Position) || b.Position <= 0 || b.Position >= 1)
                    || boundaries.Zip(boundaries.Skip(1)).Any(pair =>
                        pair.First.Value >= pair.Second.Value || pair.First.Position >= pair.Second.Position)))
            {
                return $"yearOverviewColors.{metric}Boundaries: invalid_color_boundaries";
            }
        }
        return null;
    }
}

/// <summary>
/// Pins the point <see cref="Position"/> of the way along a metric's color ramp (0 to 1,
/// exclusive) to <see cref="Value"/>, in the metric's canonical unit (mg/dL for glucose).
/// </summary>
public class YearOverviewColorBoundary
{
    public double Value { get; set; }
    public double Position { get; set; }
}
