using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nocturne.API.Reports.Derived;

/// <summary>
/// A series the API derives on the panel's grid from one or two sources. The <c>op</c>
/// discriminator selects it; each registered <see cref="IReportDerivedOp"/> names its type here, and
/// <c>ReportSpecSchemaTests</c> holds the two lists equal.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(DifferenceSpec), DifferenceSpec.Name)]
[JsonDerivedType(typeof(RatioSpec), RatioSpec.Name)]
[JsonDerivedType(typeof(ScatterSpec), ScatterSpec.Name)]
[JsonDerivedType(typeof(LagCorrelationSpec), LagCorrelationSpec.Name)]
[JsonDerivedType(typeof(RollingMeanSpec), RollingMeanSpec.Name)]
[JsonDerivedType(typeof(TrendSpec), TrendSpec.Name)]
[JsonDerivedType(typeof(BandSdSpec), BandSdSpec.Name)]
internal abstract record ReportDerivedSpec
{
    [JsonIgnore]
    public abstract string OpName { get; }

    [Description("The first source.")]
    public ReportSourceSpec A { get; init; } = null!;

    /// <summary>The second source, for an op that takes two.</summary>
    [JsonIgnore]
    public virtual ReportSourceSpec? Second => null;

    /// <summary>The sources, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<ReportSourceSpec> Sources => Second is { } b ? [A, b] : [A];
}

/// <summary>An op of two sources, paired slot by slot on the panel's grid.</summary>
internal abstract record ReportPairedSpec : ReportDerivedSpec
{
    [Description("The second source.")]
    public ReportSourceSpec B { get; init; } = null!;

    [JsonIgnore]
    public override ReportSourceSpec? Second => B;
}

/// <summary>
/// One field of a dataset query, or of a derived series (a trend of a difference, say), read over the
/// layer's range. Never a reference to another layer: the run cache reads a query once, so a source
/// and a visible layer of the same query cost one read.
/// </summary>
[Description("A source: one field of a dataset query ('dataset' and 'field'), or of one derived series ('derived' and 'field').")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReportSourceSpec
{
    [Description("The dataset to read, chosen by 'name'. Send this or 'derived'.")]
    public ReportDatasetParameters? Dataset { get; init; }

    [Description("A derived series of datasets. Send this or 'dataset'.")]
    public ReportDerivedSpec? Derived { get; init; }

    [Description("The field to use: a number field with an aggregate, or a derived series' output field.")]
    [JsonRequired]
    public string Field { get; init; } = string.Empty;

    [Description("The group of a dataset that comes in groups (the event groups of event_response): its number, from 1. Required when it has more than one.")]
    public int? Series { get; init; }
}

[Description("difference: source a minus source b, slot by slot. Both in one unit.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DifferenceSpec : ReportPairedSpec
{
    public const string Name = "difference";

    [JsonIgnore]
    public override string OpName => Name;
}

[Description("ratio: source a over source b, slot by slot; empty where b is 0.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RatioSpec : ReportPairedSpec
{
    public const string Name = "ratio";

    [JsonIgnore]
    public override string OpName => Name;
}

[Description("scatter: each slot with both sources as a point, a along the horizontal axis and b up, with the least-squares line.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ScatterSpec : ReportPairedSpec
{
    public const string Name = "scatter";

    [JsonIgnore]
    public override string OpName => Name;
}

[Description("lag_correlation: the correlation of a with b shifted by each lag from -180 to +180 minutes.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LagCorrelationSpec : ReportPairedSpec
{
    public const string Name = "lag_correlation";

    public const int MaxLagMinutes = 180;

    [JsonIgnore]
    public override string OpName => Name;
}

[Description("rolling_mean: the mean of a over the given minutes up to each slot.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RollingMeanSpec : ReportDerivedSpec
{
    public const string Name = "rolling_mean";

    [JsonIgnore]
    public override string OpName => Name;

    [Description("The window, in minutes up to each slot: 15 to 10080 (7 days).")]
    [DisplayName("Window (minutes)")]
    [Range(15, 10080)]
    public int Minutes { get; init; } = 60;
}

[Description("trend: the least-squares line of a over the horizontal axis, with its 95% confidence interval when asked for.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TrendSpec : ReportDerivedSpec
{
    public const string Name = "trend";

    [JsonIgnore]
    public override string OpName => Name;

    [Description("Also give the 95% confidence interval of the line, drawn as a band.")]
    [DisplayName("Confidence interval")]
    public bool Confidence { get; init; }
}

[Description("band_sd: the rolling mean of a over the given minutes, with one standard deviation each side as a band.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BandSdSpec : ReportDerivedSpec
{
    public const string Name = "band_sd";

    [JsonIgnore]
    public override string OpName => Name;

    [Description("The window, in minutes up to each slot: 15 to 10080 (7 days).")]
    [DisplayName("Window (minutes)")]
    [Range(15, 10080)]
    public int Minutes { get; init; } = 60;
}
