using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Scalar therapy configuration for a named profile, including DIA, carb absorption rates, units,
/// timezone, and APS-specific settings. Decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// <see cref="TherapySettings"/> holds the non-scheduled configuration for a profile. The scheduled
/// parameters (basal rates, carb ratios, ISF, target ranges) are stored in their respective schedule
/// types. All records decomposed from the same legacy <see cref="Profile"/> share the same
/// <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="Profile"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
/// <seealso cref="ProfileSummary"/>
[JsonSchemaFlatten]
public class TherapySettings : V4RecordBase
{
    /// <summary>
    /// Named profile this came from (e.g., "Default", "Weekday")
    /// </summary>
    public string ProfileName { get; set; } = "Default";

    /// <summary>
    /// Timezone for this profile
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// Blood glucose units ("mg/dL" or "mmol/L")
    /// </summary>
    public string? Units { get; set; }

    /// <summary>
    /// Duration of Insulin Action in hours
    /// </summary>
    public double Dia { get; set; } = 3.0;

    /// <summary>
    /// Carb absorption rate in grams per hour
    /// </summary>
    public int CarbsHr { get; set; } = 20;

    /// <summary>
    /// Carb absorption delay in minutes
    /// </summary>
    public int Delay { get; set; } = 20;

    /// <summary>
    /// Whether to use GI-specific carb values
    /// </summary>
    public bool? PerGIValues { get; set; }

    /// <summary>
    /// Carb absorption rate for high GI foods
    /// </summary>
    public int? CarbsHrHigh { get; set; }

    /// <summary>
    /// Carb absorption rate for medium GI foods
    /// </summary>
    public int? CarbsHrMedium { get; set; }

    /// <summary>
    /// Carb absorption rate for low GI foods
    /// </summary>
    public int? CarbsHrLow { get; set; }

    /// <summary>
    /// Delay for high GI carbs
    /// </summary>
    public int? DelayHigh { get; set; }

    /// <summary>
    /// Delay for medium GI carbs
    /// </summary>
    public int? DelayMedium { get; set; }

    /// <summary>
    /// Delay for low GI carbs
    /// </summary>
    public int? DelayLow { get; set; }

    /// <summary>
    /// Loop-specific profile settings (device tokens, dosing config, overrides)
    /// </summary>
    public LoopProfileSettings? LoopSettings { get; set; }

    /// <summary>
    /// Whether this was the default profile in the legacy store
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Who entered this profile (e.g., "Loop", "Trio")
    /// </summary>
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Whether this profile is managed by an external service (e.g., Glooko)
    /// </summary>
    public bool IsExternallyManaged { get; set; }

    /// <summary>
    /// ISO format start date preserved from legacy profile
    /// </summary>
    public string? StartDate { get; set; }
}
