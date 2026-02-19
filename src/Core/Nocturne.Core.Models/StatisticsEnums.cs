using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Interpretation of Glycemic Risk Index score
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlycomicRiskInterpretation
{
    /// <summary>Excellent glycemic control - lowest risk</summary>
    [JsonPropertyName("excellent")]
    Excellent,

    /// <summary>Good glycemic control - low risk</summary>
    [JsonPropertyName("good")]
    Good,

    /// <summary>Moderate glycemic control - moderate risk</summary>
    [JsonPropertyName("moderate")]
    Moderate,

    /// <summary>Suboptimal glycemic control - high risk</summary>
    [JsonPropertyName("suboptimal")]
    Suboptimal,

    /// <summary>Poor glycemic control - very high risk</summary>
    [JsonPropertyName("poor")]
    Poor,

    /// <summary>Unknown risk interpretation</summary>
    [JsonPropertyName("unknown")]
    Unknown,
}

/// <summary>
/// Overall clinical target assessment level
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClinicalAssessmentLevel
{
    /// <summary>Excellent - all or nearly all targets met</summary>
    [JsonPropertyName("excellent")]
    Excellent,

    /// <summary>Good - most targets met</summary>
    [JsonPropertyName("good")]
    Good,

    /// <summary>NeedsAttention - about half of targets met</summary>
    [JsonPropertyName("needsAttention")]
    NeedsAttention,

    /// <summary>NeedsSignificantImprovement - most targets not met</summary>
    [JsonPropertyName("needsSignificantImprovement")]
    NeedsSignificantImprovement,
}

/// <summary>
/// Glucose Management Indicator (GMI) interpretation level
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlucoseManagementIndicatorLevel
{
    /// <summary>Non-diabetic range</summary>
    [JsonPropertyName("nonDiabetic")]
    NonDiabetic,

    /// <summary>Prediabetes range</summary>
    [JsonPropertyName("prediabetes")]
    Prediabetes,

    /// <summary>Well-controlled diabetes</summary>
    [JsonPropertyName("wellControlled")]
    WellControlled,

    /// <summary>Moderate control</summary>
    [JsonPropertyName("moderateControl")]
    ModerateControl,

    /// <summary>Suboptimal control</summary>
    [JsonPropertyName("suboptimalControl")]
    SuboptimalControl,

    /// <summary>Poor control - intervention recommended</summary>
    [JsonPropertyName("poorControl")]
    PoorControl,
}

/// <summary>
/// Data sufficiency status
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataSufficiencyStatus
{
    /// <summary>Sufficient data for reliable analysis</summary>
    [JsonPropertyName("sufficient")]
    Sufficient,

    /// <summary>Insufficient data - may need to extend date range or check connectivity</summary>
    [JsonPropertyName("insufficientCoverage")]
    InsufficientCoverage,

    /// <summary>Large data gaps detected - analysis reliability affected</summary>
    [JsonPropertyName("largeGaps")]
    LargeGaps,
}

/// <summary>
/// Unified insight key for all clinical insight types (strengths, priority areas, actionable).
/// No JsonPropertyName — values serialize as PascalCase to match NSwag-generated TypeScript enums.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InsightKey
{
    // Strengths
    TimeInRangeExcellent,
    NoSevereHypoglycemia,
    VariabilityControlled,
    AllTargetsMet,

    // Priority areas
    ReduceSevereHypoglycemia,
    ReduceHypoglycemia,
    IncreaseTIR,
    ReduceSevereHyperglycemia,
    ReduceVariability,

    // Actionable insights
    TimeVeryLow,
    TimeBelowRange,
    TimeInRange,
    TimeVeryHigh,
    Variability,
    AllTargetsAchieved,
}
