using System.Text.Json.Serialization;
using NJsonSchema.Annotations;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Scalar therapy configuration for a named profile, including DIA, carb absorption rates, units,
/// timezone, and APS-specific settings. Decomposed from a legacy <see cref="Profile"/> record.
/// </summary>
/// <remarks>
/// <see cref="TherapySettings"/> holds the non-scheduled configuration for a profile. The scheduled
/// parameters (basal rates, carb ratios, ISF, target ranges) are stored in their respective schedule
/// types. All records decomposed from one named profile store share the same
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
public class TherapySettings : V4RecordBase, IProfileScoped
{
    /// <summary>
    /// The <c>_id</c> a row answers to as a legacy profile document: the part of its legacy id before
    /// the first colon, since a document fans out to one <c>"{_id}:{storeName}"</c> row per store; the
    /// whole legacy id when it has no colon; or the row's own id when it has no legacy id.
    /// </summary>
    public static string DocumentIdOf(TherapySettings settings) =>
        settings.LegacyId?.Contains(':') == true
            ? settings.LegacyId.Split(':')[0]
            : settings.LegacyId ?? settings.Id.ToString();

    /// <summary>
    /// Named profile this came from (e.g., "Default", "Weekday")
    /// </summary>
    public string ProfileName { get; set; } = "Default";

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

    /// <inheritdoc cref="Profile.IsAPNSProduction"/>
    public bool? IsAPNSProduction { get; set; }

    /// <summary>
    /// Whether this is the tenant's default profile: at most one row per tenant carries it.
    /// </summary>
    /// <remarks>
    /// It follows Nightscout's rule: the default is the store the newest profile document names in
    /// its <c>defaultProfile</c>, matched to the store key exactly, until a user picks another. It
    /// is a tenant-wide singleton, not per-document provenance, so ingest settles it across the
    /// tenant rather than deriving it per document. The profile in effect at a given time is not
    /// this flag: that comes from profile activations (profile switches).
    /// An update never writes it: only <c>ITherapySettingsRepository.SetDefaultAsync</c> moves it, so a
    /// write carrying a stale read of the flag cannot leave a second default.
    /// </remarks>
    public bool IsDefault { get; set; }

    /// <summary>
    /// The <c>defaultProfile</c> of the legacy profile document this row was decomposed from, shared
    /// by every row of that document; null for a row written on its own, or decomposed before it was
    /// recorded. Only decomposing the document writes it, so it is not part of the V4 API.
    /// </summary>
    [JsonIgnore]
    public string? DocumentDefaultProfile { get; set; }

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
