using System.Globalization;
using Nocturne.Core.Models;

namespace Nocturne.API.Reports.Derived;

/// <summary>A resolved source of a derived layer: its field (unit resolved), the axis it lies along and its words.</summary>
internal sealed record ReportSourceInfo(ReportField Field, ReportXKind XKind, string Label);

/// <summary>The sources of a derived layer folded onto one grid: the slot keys and each source's value in each slot.</summary>
/// <param name="Keys">The slots' x values: Unix milliseconds for time and day, minutes for an offset or a time of day.</param>
internal sealed record ReportDerivedInput(
    long[] Keys,
    double?[] A,
    double?[]? B,
    int StepMinutes,
    ReportXKind XKind,
    ReportSourceInfo SourceA,
    ReportSourceInfo? SourceB);

/// <summary>What a derived op gives: its rows, its figures, one sentence and its full text. Figures only, never a direction.</summary>
internal sealed record ReportDerivedOutput(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    IReadOnlyList<ReportFact> Facts,
    string Sentence,
    string Summary);

/// <summary>
/// One derived op: its spec type, the fields it outputs from its sources' fields, the marks that draw
/// it, and its computation, a pure function of the sources on the grid.
/// </summary>
internal interface IReportDerivedOp
{
    string Name { get; }

    string Label { get; }

    string Description { get; }

    Type SpecType { get; }

    /// <summary>1 or 2.</summary>
    int SourceCount { get; }

    /// <summary>The axis the output lies along, in words, for the catalogue.</summary>
    string XRule { get; }

    /// <summary>The output's unit, in words, for the catalogue.</summary>
    string UnitRule { get; }

    /// <summary>The axis kinds a source of it may lie along.</summary>
    IReadOnlyList<ReportXKind> SourceXKinds { get; }

    ReportXKind OutputXKind(ReportXKind source);

    IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b);

    /// <summary>The marks that may draw it, the default first.</summary>
    IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec);

    ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec);

    /// <summary>Errors in the parameters and the pairing of the sources, by the path below the derived object.</summary>
    IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b);

    ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input);
}

/// <summary>The registered derived ops, by name.</summary>
internal static class ReportDerivedOps
{
    public static IReadOnlyList<IReportDerivedOp> All { get; } =
    [
        new DifferenceOp(),
        new RatioOp(),
        new ScatterOp(),
        new LagCorrelationOp(),
        new RollingMeanOp(),
        new TrendOp(),
        new BandSdOp(),
    ];

    public static IReportDerivedOp? Find(string name) => All.FirstOrDefault(o => o.Name == name);

    public static IReportDerivedOp Of(ReportDerivedSpec spec) =>
        Find(spec.OpName) ?? throw new InvalidOperationException($"No derived op '{spec.OpName}'.");

    /// <summary>The field the output rows lay the grid's slots along.</summary>
    public const string SlotField = "x";

    internal static readonly IReadOnlyList<ReportXKind> GridKinds = [ReportXKind.Time, ReportXKind.Offset, ReportXKind.TimeOfDay, ReportXKind.Day];

    internal static ReportFieldKind FieldKindOf(ReportXKind kind) => kind switch
    {
        ReportXKind.Time => ReportFieldKind.Time,
        ReportXKind.Offset => ReportFieldKind.Offset,
        ReportXKind.TimeOfDay => ReportFieldKind.TimeOfDay,
        ReportXKind.Day => ReportFieldKind.Day,
        ReportXKind.Value => ReportFieldKind.Value,
        ReportXKind.Lag => ReportFieldKind.Lag,
        _ => ReportFieldKind.Category,
    };

    /// <summary>The slot field of an output along <paramref name="kind"/>.</summary>
    internal static ReportField Slot(ReportXKind kind) => new(SlotField, kind switch
    {
        ReportXKind.Time => "Time",
        ReportXKind.Offset => "Minutes from event",
        ReportXKind.TimeOfDay => "Time of day",
        _ => "Day",
    }, FieldKindOf(kind), kind is ReportXKind.Offset or ReportXKind.TimeOfDay ? "min" : null, "The slot of the grid.");

    /// <summary>"mg/dL/U" for mg/dL over U; no unit for one unit over itself.</summary>
    internal static string? RatioUnit(string? a, string? b) =>
        a == b ? null : b is null ? a : $"{a ?? "1"}/{b}";

    internal static string N(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);

    internal static string WithUnit(double value, string? unit, string format = "0.##") =>
        unit is null ? N(value, format) : $"{N(value, format)} {unit}";

    /// <summary>The rows of a series over the grid, one per slot with a value.</summary>
    internal static List<IReadOnlyDictionary<string, object?>> Rows(long[] keys, params (string Name, double?[] Values)[] columns)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < keys.Length; i++)
        {
            if (columns.All(c => c.Values[i] is null))
                continue;
            var row = new Dictionary<string, object?> { [SlotField] = keys[i] };
            foreach (var (name, values) in columns)
                row[name] = values[i] is { } v ? Math.Round(v, 4) : null;
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Slots a window of <paramref name="minutes"/> covers on a grid of <paramref name="step"/> minutes; a day grid counts days.</summary>
    internal static int Window(int minutes, int step, ReportXKind kind) =>
        Math.Max(1, (int)Math.Round(minutes / (double)(kind == ReportXKind.Day ? 1440 : Math.Max(1, step))));

    internal static string Words(ReportXKind kind) => kind switch
    {
        ReportXKind.Time => "time",
        ReportXKind.Offset => "minutes from an event",
        ReportXKind.TimeOfDay => "time of day",
        ReportXKind.Day => "days",
        ReportXKind.Category => "categories",
        ReportXKind.Value => "a source's values",
        ReportXKind.Lag => "lag in minutes",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

internal abstract class PairedOp : IReportDerivedOp
{
    public abstract string Name { get; }
    public abstract string Label { get; }
    public abstract string Description { get; }
    public abstract Type SpecType { get; }
    public int SourceCount => 2;
    public virtual string XRule => "The sources' axis.";
    public abstract string UnitRule { get; }
    public virtual IReadOnlyList<ReportXKind> SourceXKinds => ReportDerivedOps.GridKinds;
    public virtual ReportXKind OutputXKind(ReportXKind source) => source;
    public abstract IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b);
    public abstract IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec);
    public abstract ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec);

    public virtual IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b)
    {
        if (b is not null && a.XKind != b.XKind)
            yield return ("b", $"Both sources must lie along one axis: a lies along {ReportDerivedOps.Words(a.XKind)}, b along {ReportDerivedOps.Words(b.XKind)}.");
    }

    public abstract ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input);
}

internal sealed class DifferenceOp : PairedOp
{
    public override string Name => DifferenceSpec.Name;
    public override string Label => "Difference";
    public override string Description => "Source a minus source b, slot by slot.";
    public override Type SpecType => typeof(DifferenceSpec);
    public override string UnitRule => "The sources' unit; both sources must be in one unit.";

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        ReportDerivedOps.Slot(a.XKind),
        new("value", $"{a.Field.Label} minus {b!.Field.Label}", ReportFieldKind.Number, a.Field.Unit, "Source a minus source b in the slot.",
            Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Line, ReportMark.Area, ReportMark.Bars, ReportMark.Points];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) =>
        new() { X = ReportDerivedOps.SlotField, Y = ["value"] };

    public override IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b)
    {
        foreach (var error in base.Validate(spec, a, b))
            yield return error;
        if (b is not null && a.Field.Unit != b.Field.Unit)
            yield return ("b.field", $"A difference needs both sources in one unit: a is in {a.Field.Unit ?? "no unit"}, b in {b.Field.Unit ?? "no unit"}.");
    }

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var values = DerivedMath.Pairwise(input.A, input.B!, (x, y) => x - y);
        var present = values.OfType<double>().ToList();
        var unit = input.SourceA.Field.Unit;
        var words = $"{input.SourceA.Label} minus {input.SourceB!.Label}";
        if (present.Count == 0)
            return new([], [], "No slot has a value from both sources.", $"{words}: no slot has a value from both sources.");
        var mean = present.Average();
        var sentence = $"{words}: mean {ReportDerivedOps.WithUnit(mean, unit)} over {ReportFacts.Plural(present.Count, "slot")}, "
            + $"from {ReportDerivedOps.WithUnit(present.Min(), unit)} to {ReportDerivedOps.WithUnit(present.Max(), unit)}.";
        return new(
            ReportDerivedOps.Rows(input.Keys, ("value", values)),
            [
                new ReportFact("Mean difference", ReportDerivedOps.N(mean), unit),
                new ReportFact("Smallest", ReportDerivedOps.N(present.Min()), unit),
                new ReportFact("Largest", ReportDerivedOps.N(present.Max()), unit),
                ReportFacts.Count("Slots", present.Count),
            ],
            sentence,
            sentence);
    }
}

internal sealed class RatioOp : PairedOp
{
    public override string Name => RatioSpec.Name;
    public override string Label => "Ratio";
    public override string Description => "Source a over source b, slot by slot; empty where b is 0.";
    public override Type SpecType => typeof(RatioSpec);
    public override string UnitRule => "The unit of a over the unit of b; no unit when both are in one unit.";

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        ReportDerivedOps.Slot(a.XKind),
        new("value", $"{a.Field.Label} over {b!.Field.Label}", ReportFieldKind.Number, ReportDerivedOps.RatioUnit(a.Field.Unit, b.Field.Unit),
            "Source a over source b in the slot.", Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Line, ReportMark.Bars, ReportMark.Points];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) =>
        new() { X = ReportDerivedOps.SlotField, Y = ["value"] };

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var values = DerivedMath.Pairwise(input.A, input.B!, (x, y) => y == 0 ? null : x / y);
        var zeros = DerivedMath.Pairwise(input.A, input.B!, (_, y) => y == 0 ? 1 : 0).Count(v => v == 1);
        var present = values.OfType<double>().ToList();
        var unit = ReportDerivedOps.RatioUnit(input.SourceA.Field.Unit, input.SourceB!.Field.Unit);
        var words = $"{input.SourceA.Label} over {input.SourceB.Label}";
        var zeroNote = zeros > 0 ? $" {ReportFacts.Plural(zeros, "slot")} with {input.SourceB.Label} at 0 have no value." : string.Empty;
        if (present.Count == 0)
            return new([], [], "No slot has a ratio." + zeroNote, $"{words}: no slot has a ratio.{zeroNote}");
        var mean = present.Average();
        var sentence = $"{words}: mean {ReportDerivedOps.WithUnit(mean, unit)} over {ReportFacts.Plural(present.Count, "slot")}.{zeroNote}";
        return new(
            ReportDerivedOps.Rows(input.Keys, ("value", values)),
            [
                new ReportFact("Mean ratio", ReportDerivedOps.N(mean), unit),
                new ReportFact("Smallest", ReportDerivedOps.N(present.Min()), unit),
                new ReportFact("Largest", ReportDerivedOps.N(present.Max()), unit),
                ReportFacts.Count("Slots", present.Count),
                ReportFacts.Count("Slots with b at 0", zeros),
            ],
            sentence,
            sentence);
    }
}

internal sealed class ScatterOp : PairedOp
{
    public override string Name => ScatterSpec.Name;
    public override string Label => "Scatter";
    public override string Description => "Each slot with both sources as a point, a along the axis and b up, with the least-squares line.";
    public override Type SpecType => typeof(ScatterSpec);
    public override string XRule => "Values of source a.";
    public override string UnitRule => "Source b's unit up, source a's along the axis.";
    public override ReportXKind OutputXKind(ReportXKind source) => ReportXKind.Value;

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        new("a", a.Field.Label, ReportFieldKind.Value, a.Field.Unit, "Source a in the slot."),
        new("b", b!.Field.Label, ReportFieldKind.Number, b.Field.Unit, "Source b in the slot.", Color: b.Field.Color, Aggregate: ReportAggregate.Mean),
        new("fit", "Least-squares line", ReportFieldKind.Number, b.Field.Unit, "The least-squares line of b on a at the point.",
            Color: ChartColor.MutedForeground, Aggregate: ReportAggregate.Mean),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Points];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) =>
        new() { X = "a", Y = ["b"], Fit = "fit" };

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var pairs = DerivedMath.Pairs(input.A, input.B!);
        var r = DerivedMath.Pearson(pairs);
        var line = DerivedMath.LeastSquares(pairs);
        var a = input.SourceA;
        var b = input.SourceB!;
        var rows = pairs.OrderBy(p => p.X).Select(p => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["a"] = Math.Round(p.X, 4),
            ["b"] = Math.Round(p.Y, 4),
            ["fit"] = line is null ? null : Math.Round(line.At(p.X), 4),
        }).ToList();
        var facts = new List<ReportFact> { ReportFacts.Count("Pairs", pairs.Count) };
        if (r is not { } rValue || line is null)
        {
            var few = $"A correlation needs at least {DerivedMath.MinPairs} pairs that vary; this range has {ReportFacts.Plural(pairs.Count, "pair")}.";
            return new(rows, facts, few, $"{b.Label} against {a.Label}: {few}");
        }
        var slopeUnit = ReportDerivedOps.RatioUnit(b.Field.Unit, a.Field.Unit);
        facts.Insert(0, new ReportFact("r", ReportDerivedOps.N(rValue, "0.00")));
        facts.Add(new ReportFact("Slope", ReportDerivedOps.N(line.Slope, "0.###"), slopeUnit));
        facts.Add(new ReportFact("Intercept", ReportDerivedOps.N(line.Intercept), b.Field.Unit));
        var sentence = $"{b.Label} against {a.Label}: r = {ReportDerivedOps.N(rValue, "0.00")} over {ReportFacts.Plural(pairs.Count, "pair")}. "
            + $"The least-squares line has a slope of {ReportDerivedOps.WithUnit(line.Slope, slopeUnit, "0.###")} "
            + $"and an intercept of {ReportDerivedOps.WithUnit(line.Intercept, b.Field.Unit)}.";
        return new(rows, facts, sentence, sentence);
    }
}

internal sealed class LagCorrelationOp : PairedOp
{
    public override string Name => LagCorrelationSpec.Name;
    public override string Label => "Lag correlation";
    public override string Description => "The correlation of a with b shifted by each lag from -180 to +180 minutes.";
    public override Type SpecType => typeof(LagCorrelationSpec);
    public override string XRule => "Lag, in minutes: b after a when positive.";
    public override string UnitRule => "r, with no unit.";
    public override IReadOnlyList<ReportXKind> SourceXKinds => [ReportXKind.Time];
    public override ReportXKind OutputXKind(ReportXKind source) => ReportXKind.Lag;

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        new("lag", "Lag", ReportFieldKind.Lag, "min", "Minutes b is shifted after a; before a when negative."),
        new("r", "Correlation (r)", ReportFieldKind.Number, null, "Pearson's r of a with b shifted by the lag.",
            Color: ChartColor.Primary, Aggregate: ReportAggregate.Mean),
        new("pairs", "Pairs", ReportFieldKind.Number, null, "The slots paired at the lag.", Aggregate: ReportAggregate.Sum),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Line, ReportMark.Bars, ReportMark.Points];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) =>
        new() { X = "lag", Y = ["r"] };

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var step = Math.Max(1, input.StepMinutes);
        var most = LagCorrelationSpec.MaxLagMinutes / step;
        var lags = new List<(int Lag, double? R, int Pairs)>();
        for (var k = -most; k <= most; k++)
        {
            var pairs = DerivedMath.Shifted(input.A, input.B!, k);
            lags.Add((k * step, DerivedMath.Pearson(pairs), pairs.Count));
        }
        var rows = lags.Select(l => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["lag"] = l.Lag,
            ["r"] = l.R is { } r ? Math.Round(r, 4) : null,
            ["pairs"] = l.Pairs,
        }).ToList();
        var a = input.SourceA.Label;
        var b = input.SourceB!.Label;
        var strongest = lags.Where(l => l.R is not null).OrderByDescending(l => Math.Abs(l.R!.Value)).ThenBy(l => Math.Abs(l.Lag)).FirstOrDefault();
        if (strongest.R is not { } best)
        {
            var few = $"No lag has at least {DerivedMath.MinPairs} pairs that vary.";
            return new(rows, [], few, $"{b} against {a}, shifted from -{LagCorrelationSpec.MaxLagMinutes} to +{LagCorrelationSpec.MaxLagMinutes} minutes: {few}");
        }
        var when = strongest.Lag switch
        {
            0 => "at the same time",
            > 0 => $"with {b} {strongest.Lag} minutes after {a}",
            _ => $"with {b} {-strongest.Lag} minutes before {a}",
        };
        var sentence = $"The largest r in size is {ReportDerivedOps.N(best, "0.00")}, {when}, over {ReportFacts.Plural(strongest.Pairs, "pair")}.";
        var atZero = lags.First(l => l.Lag == 0);
        return new(
            rows,
            [
                new ReportFact("Largest r", ReportDerivedOps.N(best, "0.00")),
                new ReportFact("At lag", (strongest.Lag > 0 ? "+" : string.Empty) + strongest.Lag.ToString(CultureInfo.InvariantCulture), "min"),
                new ReportFact("r at lag 0", atZero.R is { } zero ? ReportDerivedOps.N(zero, "0.00") : "–"),
                ReportFacts.Count("Pairs at that lag", strongest.Pairs),
            ],
            sentence,
            $"{b} against {a}, shifted by each lag from -{LagCorrelationSpec.MaxLagMinutes} to +{LagCorrelationSpec.MaxLagMinutes} minutes "
            + $"every {step} minutes. {sentence}");
    }
}

internal abstract class SingleOp : IReportDerivedOp
{
    public abstract string Name { get; }
    public abstract string Label { get; }
    public abstract string Description { get; }
    public abstract Type SpecType { get; }
    public int SourceCount => 1;
    public string XRule => "The source's axis.";
    public string UnitRule => "The source's unit.";
    public IReadOnlyList<ReportXKind> SourceXKinds => ReportDerivedOps.GridKinds;
    public ReportXKind OutputXKind(ReportXKind source) => source;
    public abstract IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b);
    public abstract IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec);
    public abstract ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec);
    public virtual IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) => [];
    public abstract ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input);

    protected static IEnumerable<(string Field, string Message)> Minutes(int minutes, ReportXKind kind)
    {
        if (minutes is < 15 or > 10080)
            yield return ("minutes", "Send 15 to 10080 minutes.");
        else if (kind == ReportXKind.Day && minutes < 1440)
            yield return ("minutes", "On a day axis the window is in whole days. Send 1440 minutes (one day) or more.");
        else if (kind == ReportXKind.Day && minutes % 1440 != 0)
            yield return ("minutes", $"On a day axis the window is in whole days. Send a multiple of 1440 minutes, such as {minutes / 1440 * 1440}.");
    }
}

internal sealed class RollingMeanOp : SingleOp
{
    public override string Name => RollingMeanSpec.Name;
    public override string Label => "Rolling mean";
    public override string Description => "The mean of the source over the given minutes up to each slot.";
    public override Type SpecType => typeof(RollingMeanSpec);

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        ReportDerivedOps.Slot(a.XKind),
        new("value", $"{a.Field.Label}, rolling mean", ReportFieldKind.Number, a.Field.Unit, "The mean over the window up to the slot.",
            Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Line, ReportMark.Area];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) =>
        new() { X = ReportDerivedOps.SlotField, Y = ["value"] };

    public override IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
        Minutes(((RollingMeanSpec)spec).Minutes, a.XKind);

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var minutes = ((RollingMeanSpec)spec).Minutes;
        var (mean, _) = DerivedMath.Rolling(input.A, ReportDerivedOps.Window(minutes, input.StepMinutes, input.XKind), input.XKind == ReportXKind.TimeOfDay);
        var unit = input.SourceA.Field.Unit;
        var overall = DerivedMath.Mean(input.A);
        var sentence = overall is { } m
            ? $"{input.SourceA.Label}: the mean of the {minutes} minutes up to each slot. The mean over the range is {ReportDerivedOps.WithUnit(m, unit)}."
            : $"{input.SourceA.Label}: no values in this range.";
        return new(
            ReportDerivedOps.Rows(input.Keys, ("value", mean)),
            overall is { } o ? [new ReportFact("Mean", ReportDerivedOps.N(o), unit), new ReportFact("Window", minutes.ToString(CultureInfo.InvariantCulture), "min")] : [],
            sentence,
            sentence);
    }
}

internal sealed class TrendOp : SingleOp
{
    public override string Name => TrendSpec.Name;
    public override string Label => "Trend";
    public override string Description => "The least-squares line of the source over the axis, with its 95% confidence interval when asked for.";
    public override Type SpecType => typeof(TrendSpec);

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b)
    {
        var fields = new List<ReportField>
        {
            ReportDerivedOps.Slot(a.XKind),
            new("value", $"{a.Field.Label}, trend", ReportFieldKind.Number, a.Field.Unit, "The least-squares line at the slot.",
                Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
        };
        if (((TrendSpec)spec).Confidence)
        {
            fields.Add(new("low", "95% interval, low", ReportFieldKind.Number, a.Field.Unit, "The low edge of the line's 95% confidence interval.",
                Color: a.Field.Color, Aggregate: ReportAggregate.Mean));
            fields.Add(new("high", "95% interval, high", ReportFieldKind.Number, a.Field.Unit, "The high edge of the line's 95% confidence interval.",
                Color: a.Field.Color, Aggregate: ReportAggregate.Mean));
        }
        return fields;
    }

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) =>
        ((TrendSpec)spec).Confidence ? [ReportMark.Line, ReportMark.Band] : [ReportMark.Line];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) => mark == ReportMark.Band
        ? new ReportLayerBindings { X = ReportDerivedOps.SlotField, Y = ["value"], Edges = ["low", "high"] }
        : new ReportLayerBindings { X = ReportDerivedOps.SlotField, Y = ["value"] };

    /// <summary>A slot's position along the axis in the unit the slope is given per: hours, or days on a day axis.</summary>
    internal static double Position(long key, long first, ReportXKind kind) => kind switch
    {
        ReportXKind.Time => (key - first) / 3_600_000.0,
        ReportXKind.Day => (key - first) / 86_400_000.0,
        _ => (key - first) / 60.0,
    };

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var confidence = ((TrendSpec)spec).Confidence;
        var unit = input.SourceA.Field.Unit;
        var per = input.XKind == ReportXKind.Day ? "day" : "hour";
        if (input.Keys.Length == 0)
            return new([], [], $"{input.SourceA.Label}: no values in this range.", $"{input.SourceA.Label}: no values in this range.");
        var first = input.Keys[0];
        var pairs = new List<(double X, double Y)>();
        for (var i = 0; i < input.Keys.Length; i++)
            if (input.A[i] is { } y)
                pairs.Add((Position(input.Keys[i], first, input.XKind), y));
        var line = DerivedMath.LeastSquares(pairs);
        if (line is null)
        {
            var few = $"A line needs at least {DerivedMath.MinPairs} slots with a value; this range has {ReportFacts.Plural(pairs.Count, "slot")}.";
            return new([], [ReportFacts.Count("Slots", pairs.Count)], few, $"{input.SourceA.Label}: {few}");
        }
        var start = pairs.Min(p => p.X);
        var end = pairs.Max(p => p.X);
        var value = new double?[input.Keys.Length];
        var low = new double?[input.Keys.Length];
        var high = new double?[input.Keys.Length];
        for (var i = 0; i < input.Keys.Length; i++)
        {
            var x = Position(input.Keys[i], first, input.XKind);
            if (x < start || x > end)
                continue;
            value[i] = line.At(x);
            if (confidence)
            {
                var half = line.HalfWidth95(x);
                low[i] = value[i] - half;
                high[i] = value[i] + half;
            }
        }
        var slopeUnit = unit is null ? $"per {per}" : $"{unit} per {per}";
        IReadOnlyList<(string, double?[])> columns = confidence ? [("value", value), ("low", low), ("high", high)] : [("value", value)];
        if (line.RSquared is not { } r2)
        {
            var level = pairs[0].Y;
            var flat = $"{input.SourceA.Label} does not vary over {ReportFacts.Plural(line.N, "slot")}: it is {ReportDerivedOps.N(level, "0.###")}"
                + $"{(unit is null ? "" : $" {unit}")} in each, so the line is level.";
            return new(
                ReportDerivedOps.Rows(input.Keys, [.. columns]),
                [new ReportFact("Slope", "0", slopeUnit), ReportFacts.Count("Slots", line.N)],
                flat,
                flat);
        }
        var sentence = $"{input.SourceA.Label}: the least-squares line has a slope of {ReportDerivedOps.N(line.Slope, "0.###")} {slopeUnit} "
            + $"over {ReportFacts.Plural(line.N, "slot")}; r² = {ReportDerivedOps.N(r2, "0.00")}."
            + (confidence ? " The band is the line's 95% confidence interval." : string.Empty);
        return new(
            ReportDerivedOps.Rows(input.Keys, [.. columns]),
            [
                new ReportFact("Slope", ReportDerivedOps.N(line.Slope, "0.###"), slopeUnit),
                new ReportFact("r²", ReportDerivedOps.N(r2, "0.00")),
                ReportFacts.Count("Slots", line.N),
            ],
            sentence,
            sentence);
    }
}

internal sealed class BandSdOp : SingleOp
{
    public override string Name => BandSdSpec.Name;
    public override string Label => "Mean and standard deviation";
    public override string Description => "The rolling mean of the source over the given minutes, with one standard deviation each side as a band.";
    public override Type SpecType => typeof(BandSdSpec);

    public override IReadOnlyList<ReportField> OutputFields(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
    [
        ReportDerivedOps.Slot(a.XKind),
        new("mean", $"{a.Field.Label}, rolling mean", ReportFieldKind.Number, a.Field.Unit, "The mean over the window up to the slot.",
            Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
        new("low", "Mean minus 1 SD", ReportFieldKind.Number, a.Field.Unit, "The mean minus one standard deviation.",
            Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
        new("high", "Mean plus 1 SD", ReportFieldKind.Number, a.Field.Unit, "The mean plus one standard deviation.",
            Color: a.Field.Color, Aggregate: ReportAggregate.Mean),
    ];

    public override IReadOnlyList<ReportMark> Marks(ReportDerivedSpec spec) => [ReportMark.Band, ReportMark.Line];

    public override ReportLayerBindings DefaultBindings(ReportMark mark, ReportDerivedSpec spec) => mark == ReportMark.Band
        ? new ReportLayerBindings { X = ReportDerivedOps.SlotField, Y = ["mean"], Edges = ["low", "high"] }
        : new ReportLayerBindings { X = ReportDerivedOps.SlotField, Y = ["mean"] };

    public override IEnumerable<(string Field, string Message)> Validate(ReportDerivedSpec spec, ReportSourceInfo a, ReportSourceInfo? b) =>
        Minutes(((BandSdSpec)spec).Minutes, a.XKind);

    public override ReportDerivedOutput Compute(ReportDerivedSpec spec, ReportDerivedInput input)
    {
        var minutes = ((BandSdSpec)spec).Minutes;
        var (mean, sd) = DerivedMath.Rolling(input.A, ReportDerivedOps.Window(minutes, input.StepMinutes, input.XKind), input.XKind == ReportXKind.TimeOfDay);
        var low = DerivedMath.Pairwise(mean, sd, (m, s) => m - s);
        var high = DerivedMath.Pairwise(mean, sd, (m, s) => m + s);
        var unit = input.SourceA.Field.Unit;
        var meanSd = DerivedMath.Mean(sd);
        var sentence = meanSd is { } s
            ? $"{input.SourceA.Label}: the mean of the {minutes} minutes up to each slot, with one standard deviation each side. "
              + $"The mean standard deviation is {ReportDerivedOps.WithUnit(s, unit)}."
            : $"{input.SourceA.Label}: a standard deviation needs two values in a window; this range has none.";
        return new(
            ReportDerivedOps.Rows(input.Keys, ("mean", mean), ("low", low), ("high", high)),
            meanSd is { } v
                ? [new ReportFact("Mean SD", ReportDerivedOps.N(v), unit), new ReportFact("Window", minutes.ToString(CultureInfo.InvariantCulture), "min")]
                : [],
            sentence,
            sentence);
    }
}
