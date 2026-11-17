using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Two periods of glucose and insulin figures side by side, with each metric's change from the
/// first period to the second computed in one place.
/// </summary>
/// <seealso cref="PeriodComparisonMetric"/>
public class PeriodComparison
{
    /// <summary>The units every glucose metric in <see cref="Metrics"/> is served in.</summary>
    public GlucoseUnit Units { get; set; }

    /// <summary>The first period, the baseline of every change.</summary>
    public ComparedPeriod PeriodA { get; set; } = new();

    /// <summary>The second period.</summary>
    public ComparedPeriod PeriodB { get; set; } = new();

    /// <summary>
    /// One entry per metric. <see cref="PeriodComparisonMetric.PersonalRangePercent"/> is absent when
    /// neither period has a personal range.
    /// </summary>
    public List<MetricComparison> Metrics { get; set; } = [];

    /// <summary>
    /// Why the tenant's timezone was unavailable for either period, as
    /// <see cref="ReportAnalysisResult.TimeZoneUnavailableReason"/>. Null when a zone was applied.
    /// </summary>
    public TimeZoneUnavailableReason? TimeZoneUnavailableReason { get; set; }
}

/// <summary>One period of a <see cref="PeriodComparison"/>.</summary>
public class ComparedPeriod
{
    /// <summary>
    /// Whether the period holds any glucose readings. Without them every glucose-derived metric is
    /// absent, whatever zeros the analysis of an empty window holds.
    /// </summary>
    public bool HasData { get; set; }

    /// <summary>Time in each range. Null when <see cref="HasData"/> is false.</summary>
    public TimeInRangePercentages? TimeInRange { get; set; }
}

/// <summary>One metric across the two periods of a <see cref="PeriodComparison"/>.</summary>
public class MetricComparison
{
    /// <summary>The metric compared.</summary>
    public PeriodComparisonMetric Metric { get; set; }

    /// <summary>
    /// The first period's value at <see cref="ValueDecimals"/>. Null when the period has no data, or
    /// has none of what the metric measures (no insulin records, no carb entries, no personal range).
    /// </summary>
    public double? ValueA { get; set; }

    /// <summary>The second period's value, as <see cref="ValueA"/>.</summary>
    public double? ValueB { get; set; }

    /// <summary>
    /// <see cref="ValueB"/> less <see cref="ValueA"/>, taken on the unrounded values and rounded to
    /// <see cref="DeltaDecimals"/>. Null when either value is null. Zero means no change at the
    /// precision the metric is shown at.
    /// </summary>
    public double? Delta { get; set; }

    /// <summary>
    /// The change as a percentage of the first period's magnitude, signed. Null when either value is
    /// null or the first is zero, which admits no proportional change.
    /// </summary>
    public double? PercentChange { get; set; }

    /// <summary>Decimal places <see cref="ValueA"/> and <see cref="ValueB"/> are shown at.</summary>
    public int ValueDecimals { get; set; }

    /// <summary>Decimal places <see cref="Delta"/> is shown at.</summary>
    public int DeltaDecimals { get; set; }
}

/// <summary>A figure a <see cref="PeriodComparison"/> compares between its two periods.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeriodComparisonMetric>))]
public enum PeriodComparisonMetric
{
    /// <summary>Percentage of readings in the target range.</summary>
    TirTarget,

    /// <summary>Glucose management indicator, a percentage.</summary>
    Gmi,

    /// <summary>Coefficient of variation, a percentage.</summary>
    Cv,

    /// <summary>Glycemic risk index score.</summary>
    Gri,

    /// <summary>Mean glucose, in <see cref="PeriodComparison.Units"/>.</summary>
    Mean,

    /// <summary>Average minutes per day above range.</summary>
    HyperMinutesPerDay,

    /// <summary>Average episodes per day above range.</summary>
    HyperEventsPerDay,

    /// <summary>Average minutes per day below range.</summary>
    HypoMinutesPerDay,

    /// <summary>Average episodes per day below range.</summary>
    HypoEventsPerDay,

    /// <summary>Percentage of readings very low.</summary>
    VeryLowPercent,

    /// <summary>Percentage of readings low.</summary>
    LowPercent,

    /// <summary>Percentage of readings high.</summary>
    HighPercent,

    /// <summary>Percentage of readings very high.</summary>
    VeryHighPercent,

    /// <summary>Percentage of readings in the tenant's personal target range.</summary>
    PersonalRangePercent,

    /// <summary>Standard deviation of glucose, in <see cref="PeriodComparison.Units"/>.</summary>
    Sd,

    /// <summary>Median glucose, in <see cref="PeriodComparison.Units"/>.</summary>
    Median,

    /// <summary>Low blood glucose index.</summary>
    Lbgi,

    /// <summary>High blood glucose index.</summary>
    Hbgi,

    /// <summary>Mean amplitude of glycemic excursions, in <see cref="PeriodComparison.Units"/>.</summary>
    Mage,

    /// <summary>Continuous overlapping net glycemic action, in <see cref="PeriodComparison.Units"/>.</summary>
    Conga,

    /// <summary>Average daily risk range.</summary>
    Adrr,

    /// <summary>Total insulin per day, in units.</summary>
    InsulinPerDay,

    /// <summary>Basal insulin per day, in units.</summary>
    BasalPerDay,

    /// <summary>Basal insulin as a percentage of total insulin.</summary>
    BasalPercent,

    /// <summary>Manual boluses per day.</summary>
    BolusesPerDay,

    /// <summary>Grams of carbohydrate per day.</summary>
    CarbsPerDay,

    /// <summary>Insulin-to-carb ratio, in grams per unit.</summary>
    Icr,

    /// <summary>Percentage of the period the CGM was active.</summary>
    CgmActivePercent,

    /// <summary>Days that hold readings.</summary>
    DaysWithData,

    /// <summary>Average readings per day.</summary>
    ReadingsPerDay,
}
