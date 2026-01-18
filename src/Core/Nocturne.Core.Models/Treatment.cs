using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a Nightscout treatment entry with 1:1 legacy JavaScript compatibility
/// Compatible with both the API and Connect projects
/// </summary>
public class Treatment : ProcessableDocumentBase
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// Gets or sets the event type (e.g., "Meal Bolus", "Correction Bolus", "BG Check")
    /// </summary>
    [JsonPropertyName("eventType")]
    [Sanitizable]
    public string? EventType { get; set; }

    /// <summary>
    /// Gets or sets the treatment reason
    /// </summary>
    [JsonPropertyName("reason")]
    [Sanitizable]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the glucose value for the treatment
    /// </summary>
    [JsonPropertyName("glucose")]
    public double? Glucose { get; set; }

    /// <summary>
    /// Gets or sets the glucose type (e.g., "Finger", "Sensor")
    /// </summary>
    [JsonPropertyName("glucoseType")]
    public string? GlucoseType { get; set; }

    /// <summary>
    /// Gets or sets the carbohydrates in grams
    /// </summary>
    [JsonPropertyName("carbs")]
    public double? Carbs { get; set; }

    private double? _insulin;

    /// <summary>
    /// Gets or sets the insulin amount in units.
    /// Derives from Amount if null, or calculates from Rate * Duration.
    /// </summary>
    [JsonPropertyName("insulin")]
    public double? Insulin
    {
        get
        {
            if (_insulin.HasValue) return _insulin;
            if (_amount.HasValue) return _amount;

            // Try to calculate from Rate * Duration
            // resolving synonyms for Rate
            var r = _rate ?? _absolute;
            if (r.HasValue && _duration.HasValue && _duration.Value > 0)
            {
                return r.Value * (_duration.Value / 60.0);
            }
            return null;
        }
        set => _insulin = value;
    }

    /// <summary>
    /// Gets or sets the protein content in grams
    /// </summary>
    [JsonPropertyName("protein")]
    public double? Protein { get; set; }

    /// <summary>
    /// Gets or sets the fat content in grams
    /// </summary>
    [JsonPropertyName("fat")]
    public double? Fat { get; set; }

    /// <summary>
    /// Gets or sets the food type
    /// </summary>
    [JsonPropertyName("foodType")]
    [Sanitizable]
    public string? FoodType { get; set; }

    /// <summary>
    /// Gets or sets the units (e.g., "mg/dl", "mmol")
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    /// <summary>
    /// Gets or sets the time in milliseconds since the Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills
    {
        get
        {
            if (_mills == 0 && !string.IsNullOrEmpty(_created_at))
            {
                if (
                    DateTime.TryParse(
                        _created_at,
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsedDate
                    )
                )
                {
                    return (
                        (DateTimeOffset)DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc)
                    ).ToUnixTimeMilliseconds();
                }
            }
            return _mills;
        }
        set => _mills = value;
    }
    private long _mills;

    /// <summary>
    /// Gets or sets the created at timestamp as ISO string
    /// </summary>
    [JsonPropertyName("created_at")]
    public string? Created_at
    {
        get
        {
            if (string.IsNullOrEmpty(_created_at) && _mills > 0)
            {
                return DateTimeOffset
                    .FromUnixTimeMilliseconds(_mills)
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            return _created_at;
        }
        set => _created_at = value;
    }
    private string? _created_at;

    private double? _duration;

    /// <summary>
    /// Gets or sets the treatment duration in minutes.
    /// Calculates from Insulin / Rate if null.
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration
    {
        get
        {
            if (_duration.HasValue) return _duration;

            // Try to calculate from Insulin / Rate
            // resolving synonyms
            var i = _insulin ?? _amount;
            var r = _rate ?? _absolute;

            if (i.HasValue && r.HasValue && r.Value > 0)
            {
                return (i.Value / r.Value) * 60.0;
            }
            return null;
        }
        set => _duration = value;
    }

    /// <summary>
    /// Gets or sets the percent of temporary basal rate
    /// </summary>
    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    private double? _absolute;

    /// <summary>
    /// Gets or sets the absolute temporary basal rate.
    /// Returns Rate if this is null.
    /// </summary>
    [JsonPropertyName("absolute")]
    public double? Absolute
    {
        get => _absolute ?? Rate;
        set => _absolute = value;
    }

    /// <summary>
    /// Gets or sets the treatment notes
    /// </summary>
    [JsonPropertyName("notes")]
    [Sanitizable]
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets who entered the treatment
    /// </summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets the treatment target top
    /// </summary>
    [JsonPropertyName("targetTop")]
    public double? TargetTop { get; set; }

    /// <summary>
    /// Gets or sets the treatment target bottom
    /// </summary>
    [JsonPropertyName("targetBottom")]
    public double? TargetBottom { get; set; }

    /// <summary>
    /// Gets or sets the treatment profile
    /// </summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets whether this entry was split from another
    /// </summary>
    [JsonPropertyName("split")]
    public string? Split { get; set; }

    /// <summary>
    /// Gets or sets when this treatment was created
    /// </summary>
    [JsonPropertyName("date")]
    public long? Date { get; set; }

    /// <summary>
    /// Gets or sets the carb time offset
    /// </summary>
    [JsonPropertyName("carbTime")]
    public int? CarbTime { get; set; }

    /// <summary>
    /// Gets or sets the bolus calculator values
    /// </summary>
    [JsonPropertyName("boluscalc")]
    public Dictionary<string, object>? BolusCalc { get; set; }

    /// <summary>
    /// Gets or sets the UTCOFFSET
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp - alias for Created_at for API compatibility
    /// </summary>
    [JsonIgnore]
    public override string? CreatedAt
    {
        get => Created_at;
        set => Created_at = value;
    }

    private double? _rate;

    /// <summary>
    /// Gets or sets the timestamp as an ISO 8601 string - optional field
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Calculates Mills from Created_at if Mills is not set - for API compatibility
    /// </summary>
    [JsonIgnore]
    public long CalculatedMills
    {
        get
        {
            if (Mills > 0)
                return Mills;

            if (
                !string.IsNullOrEmpty(Created_at)
                && DateTime.TryParse(Created_at, out var createdAtDate)
            )
                return ((DateTimeOffset)createdAtDate).ToUnixTimeMilliseconds();

            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Gets or sets the profile name that cut this treatment (used by duration processing)
    /// </summary>
    [JsonPropertyName("cuttedby")]
    public string? CuttedBy { get; set; }

    /// <summary>
    /// Gets or sets the profile name that this treatment cut (used by duration processing)
    /// </summary>
    [JsonPropertyName("cutting")]
    public string? Cutting { get; set; }

    /// <summary>
    /// Gets or sets the event time as ISO string (used by Glooko connector)
    /// </summary>
    [JsonPropertyName("eventTime")]
    public string? EventTime { get; set; }

    /// <summary>
    /// Gets or sets the pre-bolus time in minutes (used by Glooko connector)
    /// </summary>
    [JsonPropertyName("preBolus")]
    public double? PreBolus { get; set; }

    /// <summary>
    /// Gets or sets the basal rate (used for temp basal treatments).
    /// If not explicitly set, checks Absolute, or attempts to calculate from Insulin / (Duration/60).
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate
    {
        get
        {
            if (_rate.HasValue) return _rate;
            if (_absolute.HasValue) return _absolute;

            // Try to calculate from Insulin / Duration
            // resolving synonyms for Insulin
            var i = _insulin ?? _amount;
            if (i.HasValue && _duration.HasValue && _duration.Value > 0)
            {
                return i.Value / (_duration.Value / 60.0);
            }

            return null;
        }
        set => _rate = value;
    }

    /// <summary>
    /// Gets or sets the blood glucose value in mg/dL
    /// </summary>
    [JsonPropertyName("mgdl")]
    public double? Mgdl { get; set; }

    /// <summary>
    /// Gets or sets the blood glucose value in mmol/L
    /// </summary>
    [JsonPropertyName("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// Gets or sets the end time in milliseconds for duration treatments
    /// </summary>
    [JsonPropertyName("endmills")]
    public long? EndMills { get; set; }

    /// <summary>
    /// Gets or sets the duration type (e.g., "indefinite")
    /// </summary>
    [JsonPropertyName("durationType")]
    [Sanitizable]
    public string? DurationType { get; set; }

    /// <summary>
    /// Gets or sets whether this treatment is an announcement
    /// </summary>
    [JsonPropertyName("isAnnouncement")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? IsAnnouncement { get; set; }

    /// <summary>
    /// Gets or sets the JSON string of profile data for profile switches
    /// </summary>
    [JsonPropertyName("profileJson")]
    [Sanitizable]
    [NocturneOnly]
    public string? ProfileJson { get; set; }

    /// <summary>
    /// Gets or sets the end profile name for profile switches
    /// </summary>
    [JsonPropertyName("endprofile")]
    [Sanitizable]
    public string? EndProfile { get; set; }

    /// <summary>
    /// Gets or sets the insulin scaling factor for adjustments
    /// </summary>
    [JsonPropertyName("insulinNeedsScaleFactor")]
    public double? InsulinNeedsScaleFactor { get; set; }

    /// <summary>
    /// Gets or sets the carb absorption time in minutes
    /// </summary>
    [JsonPropertyName("absorptionTime")]
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Gets or sets the manually entered insulin amount (for combo bolus)
    /// </summary>
    [JsonPropertyName("enteredinsulin")]
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Gets or sets the percentage of combo bolus delivered immediately
    /// </summary>
    [JsonPropertyName("splitNow")]
    public double? SplitNow { get; set; }

    /// <summary>
    /// Gets or sets the percentage of combo bolus delivered extended
    /// </summary>
    [JsonPropertyName("splitExt")]
    public double? SplitExt { get; set; }

    /// <summary>
    /// Gets or sets the treatment status
    /// </summary>
    [JsonPropertyName("status")]
    [Sanitizable]
    public string? Status { get; set; }

    private double? _relative;

    /// <summary>
    /// Gets or sets the relative basal rate change
    /// </summary>
    [JsonPropertyName("relative")]
    public double? Relative
    {
        get => _relative ?? Rate;
        set => _relative = value;
    }

    /// <summary>
    /// Gets or sets the carb ratio
    /// </summary>
    [JsonPropertyName("CR")]
    public double? CR { get; set; }

    /// <summary>
    /// Gets or sets the Nightscout client identifier
    /// </summary>
    [JsonPropertyName("NSCLIENT_ID")]
    [Sanitizable]
    public string? NsClientId { get; set; }

    /// <summary>
    /// Gets or sets whether this is the first treatment in a series
    /// </summary>
    [JsonPropertyName("first")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? First { get; set; }

    /// <summary>
    /// Gets or sets whether this is the end treatment in a series
    /// </summary>
    [JsonPropertyName("end")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? End { get; set; }

    /// <summary>
    /// Gets or sets whether this is a CircadianPercentageProfile treatment
    /// </summary>
    [JsonPropertyName("CircadianPercentageProfile")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? CircadianPercentageProfile { get; set; }

    /// <summary>
    /// Gets or sets the percentage for CircadianPercentageProfile
    /// </summary>
    [JsonPropertyName("percentage")]
    public double? Percentage { get; set; }

    /// <summary>
    /// Gets or sets the timeshift for CircadianPercentageProfile (in hours)
    /// </summary>
    [JsonPropertyName("timeshift")]
    public double? Timeshift { get; set; }

    /// <summary>
    /// Gets or sets the transmitter ID (used by CGM devices)
    /// </summary>
    [JsonPropertyName("transmitterId")]
    [Sanitizable]
    public string? TransmitterId { get; set; }

    /// <summary>
    /// Gets or sets the remote carb entry amount in grams (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteCarbs")]
    public double? RemoteCarbs { get; set; }

    /// <summary>
    /// Gets or sets the remote carb absorption time in hours (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteAbsorption")]
    public double? RemoteAbsorption { get; set; }

    /// <summary>
    /// Gets or sets the remote bolus amount in units (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteBolus")]
    public double? RemoteBolus { get; set; }

    /// <summary>
    /// Gets or sets the display name for override reason
    /// </summary>
    [JsonPropertyName("reasonDisplay")]
    [Sanitizable]
    public string? ReasonDisplay { get; set; }

    /// <summary>
    /// Gets or sets the one-time password for secure remote operations
    /// </summary>
    [JsonPropertyName("otp")]
    [Sanitizable]
    public string? Otp { get; set; }

    /// <summary>
    /// Gets or sets the sync identifier used by Loop for deduplication.
    /// This is a unique identifier that Loop uses to prevent duplicate treatments.
    /// </summary>
    [JsonPropertyName("syncIdentifier")]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the insulin type (e.g., "Humalog", "Novolog", "Fiasp").
    /// Used by Loop and other AID systems.
    /// </summary>
    [JsonPropertyName("insulinType")]
    [Sanitizable]
    public string? InsulinType { get; set; }

    /// <summary>
    /// Gets or sets whether this treatment was automatically administered by an AID system.
    /// True for automatic dosing decisions, false for user-initiated actions.
    /// </summary>
    [JsonPropertyName("automatic")]
    public bool? Automatic { get; set; }

    /// <summary>
    /// Gets or sets the temp basal type ("absolute" or "percentage").
    /// Used by Loop for temp basal treatments.
    /// </summary>
    [JsonPropertyName("temp")]
    public string? Temp { get; set; }

    private double? _amount;

    /// <summary>
    /// Gets or sets the insulin amount delivered in units.
    /// Returns Insulin if this is null.
    /// </summary>
    [JsonPropertyName("amount")]
    public double? Amount
    {
        get => _amount ?? Insulin;
        set => _amount = value;
    }

    /// <summary>
    /// Gets or sets the originally programmed insulin dose in units.
    /// May differ from amount if delivery was interrupted.
    /// </summary>
    [JsonPropertyName("programmed")]
    public double? Programmed { get; set; }

    /// <summary>
    /// Gets or sets unabsorbed insulin from previous boluses.
    /// </summary>
    [JsonPropertyName("unabsorbed")]
    public double? Unabsorbed { get; set; }

    /// <summary>
    /// Gets or sets the bolus type (e.g., "normal", "square", "dual").
    /// Maps to the standard Nightscout "type" field.
    /// </summary>
    [JsonPropertyName("type")]
    public string? BolusType { get; set; }

    /// <summary>
    /// Gets or sets the Loop-specific bolus type.
    /// Maps to the "bolusType" field sent by Loop.
    /// </summary>
    [JsonPropertyName("bolusType")]
    public string? LoopBolusType { get; set; }

    /// <summary>
    /// Gets or sets the data source identifier indicating where this treatment originated from.
    /// Use constants from <see cref="Core.Constants.DataSources"/> for consistent values.
    /// </summary>
    /// <example>
    /// Common values: "demo-service", "dexcom-connector", "manual", "mongodb-import"
    /// </example>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    // === APS/Bolus Calculator Fields ===

    /// <summary>
    /// Insulin recommended by bolus calculator specifically for carbohydrate coverage
    /// </summary>
    [JsonPropertyName("insulinRecommendationForCarbs")]
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Insulin recommended by bolus calculator for glucose correction
    /// </summary>
    [JsonPropertyName("insulinRecommendationForCorrection")]
    public double? InsulinRecommendationForCorrection { get; set; }

    /// <summary>
    /// Total insulin amount programmed for delivery (may differ from delivered if interrupted)
    /// </summary>
    [JsonPropertyName("insulinProgrammed")]
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// Actual insulin amount delivered (may be less than programmed if delivery was interrupted)
    /// </summary>
    [JsonPropertyName("insulinDelivered")]
    public double? InsulinDelivered { get; set; }

    /// <summary>
    /// Insulin on board at the time of this treatment
    /// </summary>
    [JsonPropertyName("insulinOnBoard")]
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Blood glucose input value used for bolus calculation
    /// </summary>
    [JsonPropertyName("bloodGlucoseInput")]
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (e.g., "Finger", "Sensor", "CGM")
    /// </summary>
    [JsonPropertyName("bloodGlucoseInputSource")]
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// How this bolus was calculated/initiated
    /// </summary>
    [JsonPropertyName("calculationType")]
    public CalculationType? CalculationType { get; set; }

    /// <summary>
    /// Gets or sets additional properties for the treatment
    /// </summary>
    [JsonPropertyName("additional_properties")]
    [NocturneOnly]
    public Dictionary<string, object>? AdditionalProperties { get; set; }

    /// <summary>
    /// Gets or sets the canonical group ID for deduplication.
    /// Records with the same CanonicalId represent the same underlying event from different sources.
    /// </summary>
    [JsonPropertyName("canonicalId")]
    [NocturneOnly]
    public Guid? CanonicalId { get; set; }

    /// <summary>
    /// Gets or sets the list of data sources that contributed to this unified record.
    /// Only populated when returning merged/unified DTOs.
    /// </summary>
    [JsonPropertyName("sources")]
    [NocturneOnly]
    public string[]? Sources { get; set; }
}
