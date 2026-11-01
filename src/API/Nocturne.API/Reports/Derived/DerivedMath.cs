namespace Nocturne.API.Reports.Derived;

/// <summary>
/// The numerics the derived ops share, over series aligned slot by slot on a panel's grid. A null is a
/// slot with no value; it never counts as zero.
/// </summary>
internal static class DerivedMath
{
    /// <summary>The fewest pairs a correlation or a fitted line is given for.</summary>
    public const int MinPairs = 3;

    /// <summary>Each slot of <paramref name="a"/> combined with the same slot of <paramref name="b"/>; null where either is.</summary>
    public static double?[] Pairwise(double?[] a, double?[] b, Func<double, double, double?> combine)
    {
        var result = new double?[a.Length];
        for (var i = 0; i < a.Length && i < b.Length; i++)
            result[i] = a[i] is { } x && b[i] is { } y ? combine(x, y) : null;
        return result;
    }

    /// <summary>The slots where both series have a value, as pairs.</summary>
    public static List<(double X, double Y)> Pairs(double?[] a, double?[] b)
    {
        var pairs = new List<(double, double)>();
        for (var i = 0; i < a.Length && i < b.Length; i++)
            if (a[i] is { } x && b[i] is { } y)
                pairs.Add((x, y));
        return pairs;
    }

    /// <summary>Pearson's r of the pairs, or null for fewer than <see cref="MinPairs"/> pairs or a series that does not vary.</summary>
    public static double? Pearson(IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count < MinPairs)
            return null;
        var mx = pairs.Average(p => p.X);
        var my = pairs.Average(p => p.Y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pairs)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }
        if (sxx <= 0 || syy <= 0)
            return null;
        return Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1, 1);
    }

    /// <summary>A least-squares line of y on x, with what its confidence interval needs.</summary>
    /// <param name="ResidualSe">The standard error of the residuals, on n - 2 degrees of freedom.</param>
    /// <param name="Sxx">The sum of squares of x about its mean.</param>
    public sealed record Line(double Slope, double Intercept, int N, double MeanX, double Sxx, double ResidualSe, double RSquared)
    {
        public double At(double x) => Intercept + Slope * x;

        /// <summary>The half-width of the 95% confidence interval of the line's mean at <paramref name="x"/>.</summary>
        public double HalfWidth95(double x) =>
            TQuantile975(N - 2) * ResidualSe * Math.Sqrt(1.0 / N + (x - MeanX) * (x - MeanX) / Sxx);
    }

    /// <summary>The least-squares line of the pairs, or null for fewer than <see cref="MinPairs"/> pairs or an x that does not vary.</summary>
    public static Line? LeastSquares(IReadOnlyList<(double X, double Y)> pairs)
    {
        if (pairs.Count < MinPairs)
            return null;
        var mx = pairs.Average(p => p.X);
        var my = pairs.Average(p => p.Y);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (var (x, y) in pairs)
        {
            sxy += (x - mx) * (y - my);
            sxx += (x - mx) * (x - mx);
            syy += (y - my) * (y - my);
        }
        if (sxx <= 0)
            return null;
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var residuals = pairs.Sum(p => Math.Pow(p.Y - (intercept + slope * p.X), 2));
        var se = Math.Sqrt(residuals / (pairs.Count - 2));
        var r2 = syy <= 0 ? 1 : Math.Clamp(1 - residuals / syy, 0, 1);
        return new Line(slope, intercept, pairs.Count, mx, sxx, se, r2);
    }

    /// <summary>
    /// The pairs of <paramref name="a"/> at each slot with <paramref name="b"/> <paramref name="shift"/>
    /// slots later (earlier for a negative shift).
    /// </summary>
    public static List<(double X, double Y)> Shifted(double?[] a, double?[] b, int shift)
    {
        var pairs = new List<(double, double)>();
        for (var i = 0; i < a.Length; i++)
        {
            var j = i + shift;
            if (j < 0 || j >= b.Length)
                continue;
            if (a[i] is { } x && b[j] is { } y)
                pairs.Add((x, y));
        }
        return pairs;
    }

    /// <summary>The mean and the sample standard deviation of the values in the <paramref name="window"/> slots up to each slot.</summary>
    public static (double?[] Mean, double?[] Sd) Rolling(double?[] values, int window)
    {
        var mean = new double?[values.Length];
        var sd = new double?[values.Length];
        var w = Math.Max(1, window);
        for (var i = 0; i < values.Length; i++)
        {
            double sum = 0, sumSq = 0;
            var n = 0;
            for (var k = Math.Max(0, i - w + 1); k <= i; k++)
            {
                if (values[k] is not { } v)
                    continue;
                sum += v;
                sumSq += v * v;
                n++;
            }
            if (n == 0)
                continue;
            mean[i] = sum / n;
            if (n >= 2)
                sd[i] = Math.Sqrt(Math.Max(0, (sumSq - sum * sum / n) / (n - 1)));
        }
        return (mean, sd);
    }

    /// <summary>Student's t at 97.5% for 1 to 30 degrees of freedom, as the tables give it.</summary>
    private static readonly double[] T975 =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042,
    ];

    /// <summary>
    /// The 97.5th percentile of Student's t with <paramref name="df"/> degrees of freedom: the table's
    /// value to 30, past it the Cornish-Fisher expansion about the normal's, within 0.001 of it there.
    /// </summary>
    public static double TQuantile975(int df)
    {
        if (df <= T975.Length)
            return T975[Math.Max(1, df) - 1];
        const double z = 1.959963985;
        double n = df;
        var z3 = z * z * z;
        var z5 = z3 * z * z;
        var z7 = z5 * z * z;
        return z
            + (z3 + z) / (4 * n)
            + (5 * z5 + 16 * z3 + 3 * z) / (96 * n * n)
            + (3 * z7 + 19 * z5 + 17 * z3 - 15 * z) / (384 * n * n * n);
    }

    /// <summary>The mean of the values present, or null for none.</summary>
    public static double? Mean(IEnumerable<double?> values)
    {
        double sum = 0;
        var n = 0;
        foreach (var v in values)
        {
            if (v is not { } x)
                continue;
            sum += x;
            n++;
        }
        return n == 0 ? null : sum / n;
    }
}
