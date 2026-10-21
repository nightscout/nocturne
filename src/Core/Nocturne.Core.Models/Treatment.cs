using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;
using Nocturne.Core.Models.Serializers;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models;

/// <summary>
/// Nightscout treatment entry with 1:1 legacy JavaScript compatibility.
/// Compatible with both the API and Connect projects.
/// </summary>
/// <remarks>
/// <para>Follows the mills-first timestamp pattern: <see cref="Mills"/> is computed from
/// <see cref="Created_at"/> if not explicitly set. <see cref="Created_at"/> is likewise
/// computed from Mills when not set.</para>
/// <para><see cref="Rate"/> derives from Insulin ÷ Duration and <see cref="Duration"/> from
/// Insulin ÷ Rate when not set; <see cref="Insulin"/> is never derived from the other two (see its
/// remarks). <see cref="Absolute"/> and <see cref="Amount"/> are synonyms for Rate and Insulin
/// respectively.</para>
/// </remarks>
/// <seealso cref="ProcessableDocumentBase"/>
/// <seealso cref="Entry"/>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="CalculationType"/>
public class Treatment : ProcessableDocumentBase
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    [JsonConverter(typeof(ObjectIdJsonConverter))]
    public override string? Id { get; set; }

    /// <summary>
    /// V3 API identifier - alias for Id for Nightscout V3 compatibility.
    /// Nightscout V3 API returns both _id and identifier fields with the same value.
    /// </summary>
    [JsonPropertyName("identifier")]
    [JsonConverter(typeof(ObjectIdJsonConverter))]
    public string? Identifier => Id;

    /// <summary>
    /// Server-modified timestamp for V3 compatibility.
    /// Falls back to Mills, which already resolves every other timestamp this document
    /// carries. See <see cref="V3Timestamps"/> for why it may never serialize as null.
    /// </summary>
    private long? _srvModified;

    [JsonPropertyName("srvModified")]
    public long? SrvModified
    {
        get => _srvModified ?? V3Timestamps.Resolve(Mills);
        set => _srvModified = value;
    }

    /// <summary>
    /// Server-created timestamp for V3 compatibility.
    /// </summary>
    private long? _srvCreated;

    [JsonPropertyName("srvCreated")]
    public long? SrvCreated
    {
        get => _srvCreated ?? V3Timestamps.Resolve(Mills);
        set => _srvCreated = value;
    }

    /// <summary>
    /// Event type (e.g., "Meal Bolus", "Correction Bolus", "BG Check")
    /// </summary>
    [JsonPropertyName("eventType")]
    [Sanitizable]
    public string? EventType { get; set; }

    [JsonPropertyName("reason")]
    [Sanitizable]
    public string? Reason { get; set; }

    [JsonPropertyName("glucose")]
    public double? Glucose { get; set; }

    /// <summary>
    /// Glucose type (e.g., "Finger", "Sensor")
    /// </summary>
    [JsonPropertyName("glucoseType")]
    public string? GlucoseType { get; set; }

    /// <summary>
    /// Carbohydrates in grams.
    /// Note: Nightscout V1 always includes this field even when null.
    /// </summary>
    [JsonPropertyName("carbs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Carbs { get; set; }

    private double? _insulin;

    /// <summary>
    /// Insulin amount in units. Derives from Amount if null.
    /// </summary>
    /// <remarks>
    /// Never derived from <see cref="Rate"/> × <see cref="Duration"/>: that is what a temp basal
    /// delivers, not a dose, and AAPS classifies any served record carrying <c>insulin &gt; 0</c> as a
    /// bolus before it looks at <c>eventType</c> (nssdk TreatmentMapper.toTreatment), so a temp basal
    /// served with it comes back as a phantom bolus under the temp basal's identifier.
    /// </remarks>
    [JsonPropertyName("insulin")]
    public double? Insulin
    {
        get => _insulin ?? _amount;
        set => _insulin = value;
    }

    /// <summary>
    /// Protein content in grams
    /// </summary>
    [JsonPropertyName("protein")]
    // AAPS parses protein as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? Protein { get; set; }

    /// <summary>
    /// Fat content in grams
    /// </summary>
    [JsonPropertyName("fat")]
    // AAPS parses fat as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? Fat { get; set; }

    [JsonPropertyName("foodType")]
    [Sanitizable]
    public string? FoodType { get; set; }

    /// <summary>
    /// Units (e.g., "mg/dl", "mmol")
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    /// <summary>
    /// Time in milliseconds since the Unix epoch.
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills
    {
        get => ResolveMills();
        set => _mills = value;
    }
    private long _mills;

    /// <summary>
    /// Resolves the event time in Unix milliseconds from the available timestamp fields, in
    /// precedence order: explicit <c>mills</c>, then <c>created_at</c>, then <c>eventTime</c>,
    /// then <c>timestamp</c>, then <c>date</c>. Some clients (e.g. xDrip4iOS) send only
    /// <c>eventTime</c> on certain treatments; without this fallback those records get stamped
    /// with the ingestion time, which is both wrong and defeats identity/dedup.
    /// </summary>
    private long ResolveMills()
    {
        if (_mills != 0)
            return _mills;

        if (TryParseIsoMills(_created_at, out var mills))
            return mills;
        if (TryParseIsoMills(EventTime, out mills))
            return mills;
        if (TryParseIsoMills(Timestamp, out mills))
            return mills;

        if (_date is > 0)
            return _date.Value;

        return 0;
    }

    private static bool TryParseIsoMills(string? iso, out long mills)
    {
        mills = 0;
        if (UploaderTimestamp.TryParse(iso, out var parsed))
        {
            mills = parsed.ToUnixTimeMilliseconds();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Created at timestamp as ISO string
    /// </summary>
    [JsonPropertyName("created_at")]
    public string? Created_at
    {
        get
        {
            if (string.IsNullOrEmpty(_created_at))
            {
                var mills = ResolveMills();
                if (mills > 0)
                {
                    return DateTimeOffset
                        .FromUnixTimeMilliseconds(mills)
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }
            }
            return _created_at;
        }
        set => _created_at = value;
    }
    private string? _created_at;

    private double? _duration;

    /// <summary>
    /// Treatment duration in minutes.
    /// Calculates from Insulin / Rate if null, defaults to 0.
    /// Note: Nightscout V1 always includes this field (defaults to 0).
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    // Serializes rounded to whole minutes (AAPS parses duration as a Long); the getter keeps the
    // exact value so in-memory duration math (e.g. temp-basal cutting) is not corrupted.
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? Duration
    {
        get
        {
            if (_duration.HasValue)
                return _duration;

            // Try to calculate from Insulin / Rate
            // resolving synonyms
            var i = _insulin ?? _amount;
            var r = _rate ?? _absolute;

            if (i.HasValue && r.HasValue && r.Value > 0)
            {
                return (i.Value / r.Value) * 60.0;
            }
            // Nightscout returns 0 for duration when not set
            return 0;
        }
        set => _duration = value;
    }

    /// <summary>
    /// Percent of temporary basal rate
    /// </summary>
    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    private double? _absolute;

    /// <summary>
    /// Absolute temporary basal rate.
    /// Returns Rate if this is null.
    /// </summary>
    [JsonPropertyName("absolute")]
    public double? Absolute
    {
        get => _absolute ?? Rate;
        set => _absolute = value;
    }

    [JsonPropertyName("notes")]
    [Sanitizable]
    public string? Notes { get; set; }

    /// <summary>
    /// Who entered the treatment.
    /// Note: Nightscout V1 always includes this field even when null.
    /// </summary>
    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EnteredBy { get; set; }

    [JsonPropertyName("targetTop")]
    public double? TargetTop { get; set; }

    [JsonPropertyName("targetBottom")]
    public double? TargetBottom { get; set; }

    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>
    /// Whether this entry was split from another
    /// </summary>
    [JsonPropertyName("split")]
    public string? Split { get; set; }

    private long? _date;

    /// <summary>
    /// When this treatment was created (Unix milliseconds).
    /// Falls back to Mills — which already resolves <c>created_at</c>, <c>eventTime</c> and
    /// <c>timestamp</c> — so a broadcast treatment carries the same <c>date</c> the V3 REST
    /// layer projects. <see cref="ResolveMills"/> reads the backing field directly to avoid
    /// recursing back into this getter.
    /// </summary>
    [JsonPropertyName("date")]
    public long? Date
    {
        get => _date ?? V3Timestamps.Resolve(Mills);
        set => _date = value;
    }

    /// <summary>
    /// Carb time offset
    /// </summary>
    [JsonPropertyName("carbTime")]
    public int? CarbTime { get; set; }

    /// <summary>
    /// Bolus calculator values.
    /// Note: Nightscout V1 API does not include boluscalc when empty.
    /// </summary>
    [JsonPropertyName("boluscalc")]
    [NocturneOnly]
    public Dictionary<string, object>? BolusCalc { get; set; }

    /// <summary>
    /// UTC offset in minutes.
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Creation timestamp - alias for Created_at for API compatibility
    /// </summary>
    [JsonIgnore]
    public override string? CreatedAt
    {
        get => Created_at;
        set => Created_at = value;
    }

    private double? _rate;

    /// <summary>
    /// The <c>timestamp</c> exactly as uploaded, a JSON string or number, served back with the same
    /// JSON type as Nightscout does. AAPS's v1 client uploads a temporary target's as epoch
    /// milliseconds; LoopFollow reads <c>timestamp as? String ?? created_at</c>, so a number falls back
    /// to <c>created_at</c> while the same digits as a string fail its date parse.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public JsonElement? RawTimestamp { get; set; }

    /// <summary>
    /// <see cref="RawTimestamp"/> as text: a string's value, or a number's or boolean's literal.
    /// Setting the text it already holds keeps the uploaded JSON type, so a property-by-property copy
    /// does not turn a number into a string.
    /// </summary>
    [JsonIgnore]
    public string? Timestamp
    {
        get => RawTimestamp switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
            _ => null,
        };
        set
        {
            if (value != Timestamp)
                RawTimestamp = value is null ? null : JsonSerializer.SerializeToElement(value);
        }
    }

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
                && UploaderTimestamp.TryParse(Created_at, out var createdAtDate)
            )
                return createdAtDate.ToUnixTimeMilliseconds();

            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Profile name that cut this treatment (used by duration processing)
    /// </summary>
    [JsonPropertyName("cuttedby")]
    public string? CuttedBy { get; set; }

    /// <summary>
    /// Profile name that this treatment cut (used by duration processing)
    /// </summary>
    [JsonPropertyName("cutting")]
    public string? Cutting { get; set; }

    /// <summary>
    /// Event time as ISO string (used by Glooko connector)
    /// </summary>
    [JsonPropertyName("eventTime")]
    public string? EventTime { get; set; }

    /// <summary>
    /// Pre-bolus time in minutes (used by Glooko connector)
    /// </summary>
    [JsonPropertyName("preBolus")]
    // AAPS parses preBolus as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? PreBolus { get; set; }

    /// <summary>
    /// Basal rate (used for temp basal treatments).
    /// If not explicitly set, checks Absolute, or attempts to calculate from Insulin / (Duration/60).
    /// </summary>
    [JsonPropertyName("rate")]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? Rate
    {
        get
        {
            if (_rate.HasValue)
                return _rate;
            if (_absolute.HasValue)
                return _absolute;

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
    /// Blood glucose value in mg/dL
    /// </summary>
    [JsonPropertyName("mgdl")]
    public double? Mgdl { get; set; }

    /// <summary>
    /// Blood glucose value in mmol/L
    /// </summary>
    [JsonPropertyName("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// End time in milliseconds for duration treatments
    /// </summary>
    [JsonPropertyName("endmills")]
    public long? EndMills { get; set; }

    /// <summary>
    /// Duration type (e.g., "indefinite")
    /// </summary>
    [JsonPropertyName("durationType")]
    [Sanitizable]
    public string? DurationType { get; set; }

    [JsonPropertyName("isAnnouncement")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? IsAnnouncement { get; set; }

    /// <summary>
    /// JSON string of profile data for profile switches
    /// </summary>
    [JsonPropertyName("profileJson")]
    [Sanitizable]
    public string? ProfileJson { get; set; }

    [JsonPropertyName("endprofile")]
    [Sanitizable]
    public string? EndProfile { get; set; }

    /// <summary>
    /// Insulin scaling factor for adjustments
    /// </summary>
    [JsonPropertyName("insulinNeedsScaleFactor")]
    public double? InsulinNeedsScaleFactor { get; set; }

    /// <summary>
    /// Carb absorption time in minutes
    /// </summary>
    [JsonPropertyName("absorptionTime")]
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Manually entered insulin amount (for combo bolus)
    /// </summary>
    [JsonPropertyName("enteredinsulin")]
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered immediately
    /// </summary>
    [JsonPropertyName("splitNow")]
    // AAPS parses splitNow as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? SplitNow { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered extended
    /// </summary>
    [JsonPropertyName("splitExt")]
    // AAPS parses splitExt as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? SplitExt { get; set; }

    [JsonPropertyName("status")]
    [Sanitizable]
    public string? Status { get; set; }

    private double? _relative;

    /// <summary>
    /// Relative basal rate change
    /// </summary>
    [JsonPropertyName("relative")]
    public double? Relative
    {
        get => _relative ?? Rate;
        set => _relative = value;
    }

    /// <summary>
    /// Carb ratio
    /// </summary>
    [JsonPropertyName("CR")]
    public double? CR { get; set; }

    /// <summary>
    /// Nightscout client identifier
    /// </summary>
    [JsonPropertyName("NSCLIENT_ID")]
    [Sanitizable]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? NsClientId { get; set; }

    /// <summary>
    /// Whether this is the first treatment in a series
    /// </summary>
    [JsonPropertyName("first")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? First { get; set; }

    /// <summary>
    /// Whether this is the end treatment in a series
    /// </summary>
    [JsonPropertyName("end")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? End { get; set; }

    /// <summary>
    /// Whether this is a CircadianPercentageProfile treatment
    /// </summary>
    [JsonPropertyName("CircadianPercentageProfile")]
    [JsonConverter(typeof(FlexibleBooleanJsonConverter))]
    public bool? CircadianPercentageProfile { get; set; }

    /// <summary>
    /// Percentage for CircadianPercentageProfile
    /// </summary>
    [JsonPropertyName("percentage")]
    // AAPS parses percentage as an Int; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? Percentage { get; set; }

    /// <summary>
    /// The timeshift for CircadianPercentageProfile, in the unit it was uploaded in: hours from
    /// Nightscout's careportal, milliseconds from AAPS (<see cref="ProfileSpanMetadata.UnitOfUploadedTimeshift"/>).
    /// </summary>
    [JsonPropertyName("timeshift")]
    // AAPS parses timeshift as a Long; a fractional value crashes its sync loop
    [JsonConverter(typeof(RoundedNullableDoubleConverter))]
    public double? Timeshift { get; set; }

    /// <summary>
    /// Transmitter ID (used by CGM devices)
    /// </summary>
    [JsonPropertyName("transmitterId")]
    [Sanitizable]
    public string? TransmitterId { get; set; }

    /// <summary>
    /// Remote carb entry amount in grams (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteCarbs")]
    public double? RemoteCarbs { get; set; }

    /// <summary>
    /// Remote carb absorption time in hours (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteAbsorption")]
    public double? RemoteAbsorption { get; set; }

    /// <summary>
    /// Remote bolus amount in units (for Loop remote commands)
    /// </summary>
    [JsonPropertyName("remoteBolus")]
    public double? RemoteBolus { get; set; }

    [JsonPropertyName("reasonDisplay")]
    [Sanitizable]
    public string? ReasonDisplay { get; set; }

    /// <summary>
    /// One-time password for secure remote operations
    /// </summary>
    [JsonPropertyName("otp")]
    [Sanitizable]
    public string? Otp { get; set; }

    /// <summary>
    /// Sync identifier used by Loop for deduplication.
    /// This is a unique identifier that Loop uses to prevent duplicate treatments.
    /// </summary>
    [JsonPropertyName("syncIdentifier")]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Insulin type (e.g., "Humalog", "Novolog", "Fiasp").
    /// Used by Loop and other AID systems.
    /// </summary>
    [JsonPropertyName("insulinType")]
    [Sanitizable]
    public string? InsulinType { get; set; }

    /// <summary>
    /// Whether this treatment was automatically administered by an AID system.
    /// True for automatic dosing decisions, false for user-initiated actions.
    /// </summary>
    [JsonPropertyName("automatic")]
    public bool? Automatic { get; set; }

    /// <summary>
    /// Temp basal type ("absolute" or "percentage").
    /// Used by Loop for temp basal treatments.
    /// </summary>
    [JsonPropertyName("temp")]
    public string? Temp { get; set; }

    private double? _amount;

    /// <summary>
    /// Insulin amount delivered in units.
    /// Returns Insulin if this is null.
    /// Note: Nightscout V1 API does not include amount in treatment responses.
    /// </summary>
    [JsonPropertyName("amount")]
    [NocturneOnly]
    [JsonConverter(typeof(FlexibleNullableDoubleConverter))]
    public double? Amount
    {
        get => _amount ?? Insulin;
        set => _amount = value;
    }

    /// <summary>
    /// Originally programmed insulin dose in units.
    /// May differ from amount if delivery was interrupted.
    /// </summary>
    [JsonPropertyName("programmed")]
    public double? Programmed { get; set; }

    /// <summary>
    /// Unabsorbed insulin from previous boluses.
    /// </summary>
    [JsonPropertyName("unabsorbed")]
    public double? Unabsorbed { get; set; }

    /// <summary>
    /// Bolus type (e.g., "normal", "square", "dual").
    /// Maps to the standard Nightscout "type" field.
    /// </summary>
    [JsonPropertyName("type")]
    public string? BolusType { get; set; }

    /// <summary>
    /// Loop-specific bolus type.
    /// Maps to the "bolusType" field sent by Loop.
    /// </summary>
    [JsonPropertyName("bolusType")]
    public string? LoopBolusType { get; set; }

    /// <summary>
    /// Data source identifier indicating where this treatment originated from.
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

    [JsonPropertyName("durationInMilliseconds")]
    public long? DurationInMilliseconds { get; set; }

    [JsonPropertyName("pumpId")]
    public long? PumpId { get; set; }

    [JsonPropertyName("pumpSerial")]
    public string? PumpSerial { get; set; }

    [JsonPropertyName("pumpType")]
    public string? PumpType { get; set; }

    [JsonPropertyName("endId")]
    public long? EndId { get; set; }

    [JsonPropertyName("isValid")]
    public bool? IsValid { get; set; }

    [JsonPropertyName("isReadOnly")]
    public bool? IsReadOnly { get; set; }

    [JsonPropertyName("isBasalInsulin")]
    public bool? IsBasalInsulin { get; set; }

    [JsonPropertyName("bolusCalculatorResult")]
    public string? BolusCalculatorResult { get; set; }

    [JsonPropertyName("originalDuration")]
    public int? OriginalDuration { get; set; }

    [JsonPropertyName("originalProfileName")]
    public string? OriginalProfileName { get; set; }

    [JsonPropertyName("originalPercentage")]
    public int? OriginalPercentage { get; set; }

    [JsonPropertyName("originalTimeshift")]
    public int? OriginalTimeshift { get; set; }

    [JsonPropertyName("originalCustomizedName")]
    public string? OriginalCustomizedName { get; set; }

    [JsonPropertyName("originalEnd")]
    public long? OriginalEnd { get; set; }

    /// <summary>
    /// Additional/overflow properties for the treatment.
    /// Captures unknown JSON fields (e.g., Medtronic raw_rate, raw_duration, wizard, bolus,
    /// medtronic URI, _type) so they survive round-trip through the API.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? AdditionalProperties { get; set; }

    /// <summary>
    /// Canonical group ID for deduplication.
    /// Records with the same CanonicalId represent the same underlying event from different sources.
    /// </summary>
    [JsonPropertyName("canonicalId")]
    [NocturneOnly]
    public Guid? CanonicalId { get; set; }

    /// <summary>
    /// List of data sources that contributed to this unified record.
    /// Only populated when returning merged/unified DTOs.
    /// </summary>
    [JsonPropertyName("sources")]
    [NocturneOnly]
    public string[]? Sources { get; set; }

    /// <summary>
    /// Snapshot of insulin pharmacokinetic properties at treatment delivery time.
    /// Captures DIA, peak, curve, and concentration so IOB calculations use the correct values
    /// regardless of future changes to the patient's insulin configuration.
    /// </summary>
    [NocturneOnly]
    [JsonPropertyName("insulinContext")]
    public TreatmentInsulinContext? InsulinContext { get; set; }
}
