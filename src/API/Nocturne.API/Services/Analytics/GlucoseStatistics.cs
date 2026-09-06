namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Which population a variance is taken over: a <see cref="Sample"/> divides by <c>n - 1</c>, a
/// <see cref="Population"/> by <c>n</c>. Which one a given glycemic metric is defined against is a
/// clinical question, so every caller states its own rather than inheriting a default.
/// </summary>
public enum VarianceMode
{
    /// <summary>Bessel-corrected: <c>n - 1</c>, and zero for fewer than two readings.</summary>
    Sample,

    /// <summary>Uncorrected: <c>n</c>, and <see cref="double.NaN"/> for no readings.</summary>
    Population,
}

/// <summary>
/// The arithmetic that glucose statistics are assembled from — variance, median and estimated A1C —
/// in one place, so a divisor or a formula exists once rather than once per metric.
/// </summary>
public static class GlucoseStatistics
{
    /// <summary>
    /// Variance of <paramref name="values"/> about <paramref name="mean"/>. The mean is supplied
    /// rather than derived because callers differ on whether they centre on the raw average or on
    /// the rounded one <c>StatisticsService.CalculateMean</c> returns.
    /// </summary>
    public static double Variance(IReadOnlyCollection<double> values, double mean, VarianceMode mode)
    {
        if (mode == VarianceMode.Sample && values.Count < 2)
            return 0;

        var sumOfSquares = values.Sum(value => Math.Pow(value - mean, 2));
        return sumOfSquares / (mode == VarianceMode.Sample ? values.Count - 1 : values.Count);
    }

    /// <inheritdoc cref="Variance(IReadOnlyCollection{double}, double, VarianceMode)"/>
    public static double StandardDeviation(
        IReadOnlyCollection<double> values,
        double mean,
        VarianceMode mode
    ) => Math.Sqrt(Variance(values, mean, mode));

    /// <summary>
    /// Standard deviation about the raw arithmetic mean of <paramref name="values"/>.
    /// </summary>
    public static double StandardDeviation(IReadOnlyCollection<double> values, VarianceMode mode) =>
        StandardDeviation(values, values.Average(), mode);

    /// <summary>
    /// Median of an already-sorted, non-empty series: the middle reading, or the midpoint of the
    /// two middle readings when the count is even.
    /// </summary>
    public static double Median(IReadOnlyList<double> sortedValues) =>
        sortedValues.Count % 2 == 0
            ? (sortedValues[sortedValues.Count / 2 - 1] + sortedValues[sortedValues.Count / 2]) / 2.0
            : sortedValues[sortedValues.Count / 2];

    /// <summary>
    /// Whether <paramref name="mgdl"/> is a glucose reading at all. A <c>&gt; 0</c> test does not
    /// settle it: PostgreSQL orders NaN above every number, so a NaN stored in a
    /// <c>double precision</c> column passes that test when it runs in SQL. Admitted into a
    /// series, a NaN fails every bound of a <see cref="GlucoseZoneScale"/> and so is classified
    /// into the remainder zone — severe hyperglycaemia, on a scale that ends there — and makes any
    /// mean taken over the series NaN.
    /// </summary>
    public static bool IsReading(double mgdl) => mgdl > 0 && !double.IsNaN(mgdl);

    /// <summary>
    /// Estimated A1C as a percentage from mean glucose in mg/dL, by the ADAG regression
    /// <c>(mean + 46.7) / 28.7</c>. A mean of zero means there were no readings, and reports zero
    /// rather than the 1.6% the regression would give.
    /// </summary>
    public static double EstimatedA1C(double meanGlucose) =>
        meanGlucose == 0 ? 0 : (meanGlucose + 46.7) / 28.7;
}

/// <summary>
/// One bound of a <see cref="GlucoseZoneScale"/>: the reading admitted into that zone is the one
/// on the named side of <paramref name="Threshold"/>, with the edge itself belonging to the zone
/// only when <paramref name="Inclusive"/>.
/// </summary>
public readonly record struct GlucoseZoneBound(double Threshold, bool Above, bool Inclusive)
{
    /// <summary>Readings strictly below <paramref name="threshold"/>.</summary>
    public static GlucoseZoneBound Under(double threshold) => new(threshold, Above: false, Inclusive: false);

    /// <summary>Readings up to and including <paramref name="threshold"/>.</summary>
    public static GlucoseZoneBound UpTo(double threshold) => new(threshold, Above: false, Inclusive: true);

    /// <summary>Readings strictly above <paramref name="threshold"/>.</summary>
    public static GlucoseZoneBound Over(double threshold) => new(threshold, Above: true, Inclusive: false);

    internal bool Admits(double value) =>
        Above
            ? (Inclusive ? value >= Threshold : value > Threshold)
            : (Inclusive ? value <= Threshold : value < Threshold);
}

/// <summary>
/// An ordered set of glucose zones, given as the bounds that separate them. A reading falls into
/// the first zone whose bound admits it, and into a final remainder zone if none does — so the
/// scale is the if-chain the zone sets were written as, with the edges as data.
/// <para>
/// Order is significant, and is the caller's: a scale that tests <c>&gt; veryHigh</c> before
/// <c>&gt; targetTop</c> classifies differently from one that does not when a tenant configures
/// the two out of order, so each caller lists its bounds in the order it has always tested them.
/// Zones are identified by position, which the caller names with an enum whose members follow the
/// same order, the remainder last.
/// </para>
/// </summary>
public sealed class GlucoseZoneScale(params GlucoseZoneBound[] bounds)
{
    /// <summary>Number of zones, counting the remainder.</summary>
    public int ZoneCount => bounds.Length + 1;

    /// <summary>Zero-based index of the zone <paramref name="value"/> falls into.</summary>
    public int Classify(double value)
    {
        for (var index = 0; index < bounds.Length; index++)
        {
            if (bounds[index].Admits(value))
                return index;
        }

        return bounds.Length;
    }

    /// <summary>Readings per zone, indexed as <see cref="Classify"/> returns.</summary>
    public int[] Count(IEnumerable<double> values)
    {
        var counts = new int[ZoneCount];

        foreach (var value in values)
            counts[Classify(value)]++;

        return counts;
    }
}
