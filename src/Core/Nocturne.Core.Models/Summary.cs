using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Summary response model providing aggregated glucose, treatment, profile, and state data.
/// Provides 1:1 backwards compatibility with the legacy <c>/api/v2/summary</c> endpoint.
/// </summary>
/// <seealso cref="SummaryTreatments"/>
/// <seealso cref="SummaryState"/>
/// <seealso cref="SummarySgv"/>
public class SummaryResponse
{
    /// <summary>
    /// Processed sensor glucose values within the time window
    /// </summary>
    [JsonPropertyName("sgvs")]
    public List<SummarySgv> Sgvs { get; set; } = new();

    /// <summary>
    /// Processed treatments including insulin/carbs and temp basals
    /// </summary>
    [JsonPropertyName("treatments")]
    public SummaryTreatments Treatments { get; set; } = new();

    [JsonPropertyName("profile")]
    public object? Profile { get; set; }

    /// <summary>
    /// Current state information including IOB, COB, and device ages
    /// </summary>
    [JsonPropertyName("state")]
    public SummaryState State { get; set; } = new();
}

/// <summary>
/// Simplified sensor glucose value for summary endpoint
/// Contains only essential glucose data
/// </summary>
public class SummarySgv
{
    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    [JsonPropertyName("sgv")]
    public int Sgv { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Noise level (only included if not 1)
    /// </summary>
    [JsonPropertyName("noise")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Noise { get; set; }
}

/// <summary>
/// Treatment summary containing insulin/carbs treatments and temp basals
/// </summary>
public class SummaryTreatments
{
    /// <summary>
    /// Processed temporary basal entries
    /// </summary>
    [JsonPropertyName("tempBasals")]
    public List<SummaryTempBasal> TempBasals { get; set; } = new();

    /// <summary>
    /// Treatments containing insulin or carbs
    /// </summary>
    [JsonPropertyName("treatments")]
    public List<SummaryTreatment> Treatments { get; set; } = new();

    /// <summary>
    /// Temporary target entries
    /// </summary>
    [JsonPropertyName("targets")]
    public List<SummaryTarget> Targets { get; set; } = new();
}

/// <summary>
/// Simplified treatment entry for summary endpoint
/// Contains only insulin and carbs data
/// </summary>
public class SummaryTreatment
{
    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Carbohydrate amount in grams
    /// </summary>
    [JsonPropertyName("carbs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Carbs { get; set; }

    /// <summary>
    /// Insulin amount in units
    /// </summary>
    [JsonPropertyName("insulin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Insulin { get; set; }
}

/// <summary>
/// Processed temporary basal entry
/// </summary>
public class SummaryTempBasal
{
    /// <summary>
    /// Start timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary>
    /// Duration in seconds
    /// </summary>
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>
    /// Absolute basal rate in units per hour
    /// </summary>
    [JsonPropertyName("absolute")]
    public double Absolute { get; set; }

    /// <summary>
    /// Whether this is a profile basal (1 if from profile, null otherwise)
    /// </summary>
    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Profile { get; set; }
}

/// <summary>
/// Temporary target entry
/// </summary>
public class SummaryTarget
{
    /// <summary>
    /// Target top value in mg/dL
    /// </summary>
    [JsonPropertyName("targetTop")]
    public int TargetTop { get; set; }

    /// <summary>
    /// Target bottom value in mg/dL
    /// </summary>
    [JsonPropertyName("targetBottom")]
    public int TargetBottom { get; set; }

    /// <summary>
    /// Duration in seconds
    /// </summary>
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public long Mills { get; set; }
}

/// <summary>
/// Current state information including IOB, COB, and device ages
/// </summary>
public class SummaryState
{
    /// <summary>
    /// Insulin on board in units
    /// </summary>
    [JsonPropertyName("iob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Iob { get; set; }

    /// <summary>
    /// Carbs on board in grams
    /// </summary>
    [JsonPropertyName("cob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Cob { get; set; }

    /// <summary>
    /// Bolus wizard preview estimate in units
    /// </summary>
    [JsonPropertyName("bwp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double Bwp { get; set; }

    /// <summary>
    /// Cannula age in hours
    /// </summary>
    [JsonPropertyName("cage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cage { get; set; }

    /// <summary>
    /// Sensor age in hours
    /// </summary>
    [JsonPropertyName("sage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Sage { get; set; }

    /// <summary>
    /// Insulin age in hours
    /// </summary>
    [JsonPropertyName("iage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Iage { get; set; }

    /// <summary>
    /// Battery age in hours
    /// </summary>
    [JsonPropertyName("bage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Bage { get; set; }

    /// <summary>
    /// Battery level percentage
    /// </summary>
    [JsonPropertyName("battery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Battery { get; set; }

    /// <summary>
    /// Calibration age in hours
    /// </summary>
    [JsonPropertyName("calib_age")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CalibAge { get; set; }

    /// <summary>
    /// Time until sensor expires in hours
    /// </summary>
    [JsonPropertyName("sensor_expires_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SensorExpiresIn { get; set; }
}
