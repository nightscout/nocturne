using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.API.Reports.Derived;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>The derived ops as pure functions over series on a grid: known answers, gaps, zero divisors and shifts.</summary>
public class ReportDerivedOpsTests
{
    private static readonly ReportField Glucose = new("glucose", "Glucose", ReportFieldKind.Number, "mg/dL", "g", Aggregate: ReportAggregate.Mean);
    private static readonly ReportField Iob = new("iob", "IOB", ReportFieldKind.Number, "U", "i", Aggregate: ReportAggregate.Mean);

    private static ReportSourceInfo Info(ReportField field) => new(field, ReportXKind.Time, field.Label);

    private static ReportDerivedInput Input(double?[] a, double?[]? b = null, int step = 5, ReportField? fa = null, ReportField? fb = null) => new(
        [.. Enumerable.Range(0, a.Length).Select(i => (long)i * step * 60_000)],
        a, b, step, ReportXKind.Time, Info(fa ?? Glucose), b is null ? null : Info(fb ?? Glucose));

    private static double? Value(IReadOnlyDictionary<string, object?> row, string name) => row[name] as double?;

    [Fact]
    public void A_difference_is_a_minus_b_slot_by_slot_and_empty_where_either_is()
    {
        var output = new DifferenceOp().Compute(new DifferenceSpec(), Input([100, 120, null, 150], [90, null, 80, 100]));

        output.Rows.Select(r => (Value(r, "value"), (long)r["x"]!)).Should().Equal((10.0, 0L), (50.0, 15 * 60_000L));
        output.Facts.Single(f => f.Label == "Mean difference").Value.Should().Be("30");
        output.Sentence.Should().Be("Glucose minus Glucose: mean 30 mg/dL over 2 slots, from 10 mg/dL to 50 mg/dL.");
    }

    [Fact]
    public void A_difference_with_no_paired_slot_says_so()
    {
        var output = new DifferenceOp().Compute(new DifferenceSpec(), Input([100, null], [null, 90]));

        output.Rows.Should().BeEmpty();
        output.Sentence.Should().Be("No slot has a value from both sources.");
    }

    [Fact]
    public void A_ratio_is_empty_where_b_is_zero_and_counts_those_slots()
    {
        var output = new RatioOp().Compute(new RatioSpec(), Input([100, 50, 30], [2, 0, 3], fb: Iob));

        output.Rows.Select(r => Value(r, "value")).Should().Equal(50.0, 10.0);
        output.Facts.Single(f => f.Label == "Slots with b at 0").Value.Should().Be("1");
        output.Facts.Single(f => f.Label == "Mean ratio").Unit.Should().Be("mg/dL/U");
        output.Sentence.Should().Contain("1 slot with IOB at 0 have no value");
    }

    [Fact]
    public void A_scatter_gives_r_n_slope_and_intercept_of_a_known_line()
    {
        double?[] a = [0, 1, 2, 3, 4, null];
        double?[] b = [10, 12, 14, 16, 18, 99];

        var output = new ScatterOp().Compute(new ScatterSpec(), Input(a, b, fa: Iob));

        output.Rows.Should().HaveCount(5, "the slot without a is not a pair");
        output.Rows.Select(r => Value(r, "fit")).Should().Equal(10.0, 12.0, 14.0, 16.0, 18.0);
        var facts = output.Facts.ToDictionary(f => f.Label);
        facts["r"].Value.Should().Be("1.00");
        facts["Pairs"].Value.Should().Be("5");
        facts["Slope"].Value.Should().Be("2");
        facts["Slope"].Unit.Should().Be("mg/dL/U");
        facts["Intercept"].Value.Should().Be("10");
    }

    [Fact]
    public void A_scatter_of_a_series_that_does_not_vary_gives_no_r()
    {
        var output = new ScatterOp().Compute(new ScatterSpec(), Input([1, 1, 1, 1], [5, 6, 7, 8]));

        output.Facts.Select(f => f.Label).Should().Equal("Pairs");
        output.Sentence.Should().Contain("needs at least 3 pairs that vary");
    }

    [Fact]
    public void Pearson_r_matches_a_hand_computed_value()
    {
        var r = DerivedMath.Pearson([(1, 2), (2, 4), (3, 5), (4, 4), (5, 5)]);

        r.Should().BeApproximately(0.7746, 0.0001);
        DerivedMath.Pearson([(1, 2), (2, 3)]).Should().BeNull("two pairs are too few");
    }

    [Fact]
    public void A_lag_correlation_finds_the_shift_of_a_copy()
    {
        var random = new Random(7);
        var a = Enumerable.Range(0, 300).Select(_ => (double?)random.NextDouble() * 10).ToArray();
        // b is a, 45 minutes later: b at t is a at t - 45.
        var b = Enumerable.Range(0, 300).Select(i => i >= 9 ? a[i - 9] : null).ToArray();

        var output = new LagCorrelationOp().Compute(new LagCorrelationSpec(), Input(a, b));

        output.Rows.Should().HaveCount(73, "every 5 minutes from -180 to +180");
        var facts = output.Facts.ToDictionary(f => f.Label);
        facts["At lag"].Value.Should().Be("+45");
        facts["Largest r"].Value.Should().Be("1.00");
        output.Sentence.Should().Be("The largest r in size is 1.00, with Glucose 45 minutes after Glucose, over 291 pairs.");
    }

    [Fact]
    public void A_lag_correlation_finds_a_negative_correlation_by_its_size()
    {
        var a = Enumerable.Range(0, 100).Select(i => (double?)Math.Sin(i / 3.0)).ToArray();
        var b = a.Select(v => -v).ToArray();

        var facts = new LagCorrelationOp().Compute(new LagCorrelationSpec(), Input(a, b)).Facts.ToDictionary(f => f.Label);

        facts["Largest r"].Value.Should().Be("-1.00");
        facts["At lag"].Value.Should().Be("0");
    }

    [Fact]
    public void A_rolling_mean_averages_the_window_up_to_each_slot_skipping_gaps()
    {
        var output = new RollingMeanOp().Compute(new RollingMeanSpec { Minutes = 15 }, Input([3, null, 9, 12, null, null, null]));

        output.Rows.Select(r => Value(r, "value")).Should().Equal(3.0, 3.0, 6.0, 10.5, 10.5, 12.0);
        output.Rows.Should().HaveCount(6, "the last slot's window holds no value");
    }

    [Fact]
    public void A_band_of_one_sd_is_the_rolling_mean_plus_and_minus_the_sample_sd()
    {
        var output = new BandSdOp().Compute(new BandSdSpec { Minutes = 15 }, Input([2, 4, 6]));

        var last = output.Rows[^1];
        Value(last, "mean").Should().Be(4);
        Value(last, "low").Should().Be(2);
        Value(last, "high").Should().Be(6);
        Value(output.Rows[0], "low").Should().BeNull("one value has no standard deviation");
    }

    [Fact]
    public void A_trend_fits_a_line_per_hour_with_a_confidence_band_that_holds_the_line()
    {
        double?[] values = [100, 103, 101, 106, 104, 109, 107, 112, 110, 115, 113, 118];

        var output = new TrendOp().Compute(new TrendSpec { Confidence = true }, Input(values, step: 60));

        var facts = output.Facts.ToDictionary(f => f.Label);
        facts["Slope"].Unit.Should().Be("mg/dL per hour");
        double.Parse(facts["Slope"].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(1.5, 0.1);
        output.Rows.Should().OnlyContain(r => Value(r, "low") < Value(r, "value") && Value(r, "value") < Value(r, "high"));
        var middle = output.Rows[5];
        var edge = output.Rows[0];
        (Value(middle, "high")! - Value(middle, "low")!).Should().BeLessThan((double)(Value(edge, "high")! - Value(edge, "low")!),
            "the interval is narrowest near the mean of x");
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(3.9)]
    public void A_constant_that_rounding_leaves_a_little_spread_has_no_line_or_correlation(double constant)
    {
        var flat = Enumerable.Repeat(constant, 50).Select((v, i) => (v * (1 + i) / (1 + i), 100.0 + i)).ToList();

        DerivedMath.LeastSquares(flat).Should().BeNull();
        DerivedMath.Pearson(flat).Should().BeNull();
        DerivedMath.Pearson([.. flat.Select(p => (p.Item2, p.Item1))]).Should().BeNull();
    }

    [Fact]
    public void A_rolling_window_on_a_time_of_day_axis_runs_back_past_midnight()
    {
        double?[] day = [10, 20, 30, 40];

        DerivedMath.Rolling(day, 2, wrap: true).Mean.Should().Equal(25.0, 15.0, 25.0, 35.0);
        DerivedMath.Rolling(day, 2).Mean.Should().Equal(10.0, 15.0, 25.0, 35.0);
    }

    [Fact]
    public void A_t_quantile_is_close_to_the_tables()
    {
        DerivedMath.TQuantile975(3).Should().Be(3.182);
        DerivedMath.TQuantile975(10).Should().Be(2.228);
        DerivedMath.TQuantile975(40).Should().BeApproximately(2.021, 0.001);
        DerivedMath.TQuantile975(120).Should().BeApproximately(1.980, 0.001);
        DerivedMath.TQuantile975(1000).Should().BeApproximately(1.962, 0.001);
    }

    /// <summary>Words that would turn a figure into advice; a derived layer states figures only.</summary>
    private static readonly string[] Directions =
        ["consider", "suggest", "should", "adjust", "increase", "decrease", "recommend", "too high", "too low", "better", "worse", "try "];

    [Fact]
    public void No_derived_sentence_or_fact_gives_a_direction()
    {
        var rising = Enumerable.Range(0, 60).Select(i => (double?)(100 + i * 2 + (i % 3))).ToArray();
        var falling = rising.Select(v => (double?)(300 - v)).ToArray();
        var gappy = rising.Select((v, i) => i % 4 == 0 ? null : v).ToArray();
        var flat = Enumerable.Repeat((double?)5, 60).ToArray();
        var zeros = Enumerable.Repeat((double?)0, 60).ToArray();
        var texts = new List<string>();
        foreach (var op in ReportDerivedOps.All)
        {
            var spec = ReportCatalogue.Widest(op);
            foreach (var (a, b) in new[] { (rising, falling), (falling, rising), (gappy, rising), (flat, zeros), (zeros, flat), (Array.Empty<double?>(), Array.Empty<double?>()) })
            {
                var output = op.Compute(spec, Input(a, op.SourceCount == 2 ? b : null, fb: Iob));
                texts.Add(output.Sentence);
                texts.Add(output.Summary);
                texts.AddRange(output.Facts.SelectMany(f => new[] { f.Label, f.Note ?? "", f.Unit ?? "" }));
            }
            texts.Add(op.Label);
            texts.Add(op.Description);
            texts.AddRange(op.OutputFields(spec, Info(Glucose), Info(Iob)).SelectMany(f => new[] { f.Label, f.Description }));
        }

        texts.Should().NotBeEmpty();
        foreach (var text in texts)
            Directions.Where(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(text);
    }

    /// <summary>
    /// The cells of period_comparison's direction column: which way a figure moved against the clinical
    /// consensus for it, a value in a table cell, never a sentence.
    /// </summary>
    private static readonly HashSet<string> DirectionCells =
    [
        "better",
        "worse",
        "better or worse: whether B's value is better than A's for this figure; empty where neither a rise nor a fall is better, or nothing changed.",
    ];

    /// <summary>
    /// Every string the datasets and the runner write, as source text: a sentence, fact or legend line any of
    /// them builds reaches a panel's headline through one of these, whichever branch a run takes.
    /// </summary>
    [Fact]
    public void No_dataset_sentence_or_fact_template_gives_a_direction()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "API", "Nocturne.API", "Reports")))
            root = root.Parent;
        root.Should().NotBeNull("the tests run inside the repository");
        var reports = Path.Combine(root!.FullName, "src", "API", "Nocturne.API", "Reports");
        var files = Directory.GetFiles(Path.Combine(reports, "Datasets"), "*.cs")
            .Concat(new[] { "GlucoseSummary.cs", "ReportFacts.cs", "ReportSpecRunner.cs" }.Select(f => Path.Combine(reports, f)))
            .ToList();

        var literals = files.SelectMany(file => File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .SelectMany(line => Regex.Matches(line, @"""(?:[^""\\]|\\.)*""").Select(m => m.Value.Trim('"')))
                .Select(text => (File: Path.GetFileName(file), Text: text)))
            .ToList();

        literals.Should().HaveCountGreaterThan(500);
        foreach (var (file, text) in literals.Where(l => !(l.File == "PeriodComparisonDataset.cs" && DirectionCells.Contains(l.Text))))
            Directions.Where(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty($"{file}: {text}");
    }
}
