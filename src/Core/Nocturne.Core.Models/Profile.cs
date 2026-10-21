using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Nightscout profile record for the API.
/// Compatible with the legacy Nightscout profiles collection.
/// A profile record contains a named store of basal rates, carb ratios, insulin sensitivities,
/// and target ranges. Multiple named profiles can be stored in a single record via <see cref="Store"/>.
/// </summary>
/// <seealso cref="ProfileData"/>
/// <seealso cref="TimeValue"/>
/// <seealso cref="LoopProfileSettings"/>
public class Profile
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>
    /// Name of the default profile in the store
    /// </summary>
    [JsonPropertyName("defaultProfile")]
    public string DefaultProfile { get; set; } = "Default";

    /// <summary>
    /// When the document applies from, as the client sent it; null when it sent none.
    /// </summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    /// <summary>
    /// Time in milliseconds since the Unix epoch
    /// Nightscout may send this as either a number or a string
    /// </summary>
    [JsonPropertyName("mills")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Mills { get; set; }

    /// <summary>
    /// Server-modified timestamp (Unix milliseconds). V3 compatibility field.
    /// Falls back to <see cref="FallbackTimestampMills"/> — profiles are often uploaded
    /// without mills.
    /// </summary>
    private long? _srvModified;

    [JsonPropertyName("srvModified")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? SrvModified
    {
        get => _srvModified ?? FallbackTimestampMills();
        set => _srvModified = value;
    }

    /// <summary>
    /// Server-created timestamp (Unix milliseconds). V3 compatibility field.
    /// </summary>
    private long? _srvCreated;

    [JsonPropertyName("srvCreated")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? SrvCreated
    {
        get => _srvCreated ?? FallbackTimestampMills();
        set => _srvCreated = value;
    }

    private long? FallbackTimestampMills() =>
        V3Timestamps.Resolve(Mills, StartDate, CreatedAt);

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// <c>false</c> on a deleted document, which a v3 history read still returns so a syncing client
    /// learns of the delete; unset on a live one, as Nightscout leaves it.
    /// </summary>
    [JsonPropertyName("isValid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsValid { get; set; }

    /// <summary>
    /// Units used for blood glucose values (mg/dL or mmol/L)
    /// </summary>
    [JsonPropertyName("units")]
    public string Units { get; set; } = "mg/dL";

    [JsonPropertyName("store")]
    public Dictionary<string, ProfileData> Store { get; set; } = new();

    /// <summary>
    /// Who entered this profile (e.g., "Loop")
    /// Required for Nightscout compatibility
    /// </summary>
    [JsonPropertyName("enteredBy")]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Loop-specific settings for this profile.
    /// This is critical for Loop compatibility - contains device tokens, dosing settings,
    /// override presets, and schedule overrides.
    /// </summary>
    [JsonPropertyName("loopSettings")]
    public LoopProfileSettings? LoopSettings { get; set; }

    /// <summary>
    /// The APNs environment the uploading Loop build registered its device token with: <c>true</c>
    /// for production (TestFlight, App Store), <c>false</c> for sandbox (Xcode). Unset when the
    /// uploader did not say, so Loop pushes fall back to the configured environment.
    /// </summary>
    [JsonPropertyName("isAPNSProduction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsAPNSProduction { get; set; }

    /// <summary>
    /// Whether this profile is managed by an external service (e.g. Glooko)
    /// </summary>
    [NocturneOnly]
    public bool IsExternallyManaged { get; set; }

    /// <summary>
    /// Whether this profile was converted on the fly from legacy format
    /// </summary>
    [JsonIgnore]
    public bool ConvertedOnTheFly { get; set; }
}

/// <summary>
/// Data for a specific named profile within a profile record
/// </summary>
public class ProfileData
{
    /// <summary>
    /// Duration of insulin action in hours
    /// </summary>
    [JsonPropertyName("dia")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Dia { get; set; } = 3.0;

    /// <summary>
    /// Carbs absorption rate in grams per hour
    /// </summary>
    [JsonPropertyName("carbs_hr")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int CarbsHr { get; set; } = 20;

    /// <summary>
    /// Carb absorption delay in minutes
    /// </summary>
    [JsonPropertyName("delay")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Delay { get; set; } = 20;

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>
    /// Units used for blood glucose values
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    /// <summary>
    /// Whether to use GI-specific carb values
    /// </summary>
    [JsonPropertyName("perGIvalues")]
    public bool? PerGIValues { get; set; }

    /// <summary>
    /// Carbs absorption rate for high GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_high")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrHigh { get; set; }

    /// <summary>
    /// Carbs absorption rate for medium GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_medium")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrMedium { get; set; }

    /// <summary>
    /// Carbs absorption rate for low GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_low")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrLow { get; set; }

    /// <summary>
    /// Delay for high GI carbs
    /// </summary>
    [JsonPropertyName("delay_high")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayHigh { get; set; }

    /// <summary>
    /// Delay for medium GI carbs
    /// </summary>
    [JsonPropertyName("delay_medium")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayMedium { get; set; }

    /// <summary>
    /// Delay for low GI carbs
    /// </summary>
    [JsonPropertyName("delay_low")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayLow { get; set; }

    /// <summary>
    /// Basal rates throughout the day
    /// </summary>
    [JsonPropertyName("basal")]
    public List<TimeValue> Basal { get; set; } = new();

    /// <summary>
    /// Carb ratios throughout the day
    /// </summary>
    [JsonPropertyName("carbratio")]
    public List<TimeValue> CarbRatio { get; set; } = new();

    /// <summary>
    /// Insulin sensitivity factors throughout the day
    /// </summary>
    [JsonPropertyName("sens")]
    public List<TimeValue> Sens { get; set; } = new();

    /// <summary>
    /// Low blood glucose targets throughout the day
    /// </summary>
    [JsonPropertyName("target_low")]
    public List<TimeValue> TargetLow { get; set; } = new();

    /// <summary>
    /// High blood glucose targets throughout the day
    /// </summary>
    [JsonPropertyName("target_high")]
    public List<TimeValue> TargetHigh { get; set; } = new();

    /// <summary>
    /// Whether this profile was converted on the fly from legacy format
    /// </summary>
    [JsonIgnore]
    public bool ConvertedOnTheFly { get; set; }
}

/// <summary>
/// Time-based value used in profiles (e.g., basal rates, carb ratios)
/// </summary>
public class TimeValue
{
    /// <summary>
    /// Time in HH:mm format (e.g., "06:00")
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "00:00";

    [JsonPropertyName("value")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Value { get; set; }

    /// <summary>
    /// Time converted to seconds since midnight for faster calculations.
    /// This property is included in JSON output for Nightscout compatibility.
    /// If not set, it will be calculated from Time property during serialization.
    /// </summary>
    [JsonPropertyName("timeAsSeconds")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? TimeAsSeconds { get; set; }

    /// <summary>
    /// Populates <see cref="TimeAsSeconds"/> from <see cref="Time"/> if it has not already been set.
    /// </summary>
    /// <remarks>
    /// Call before serialization to ensure the <c>timeAsSeconds</c> field is included in the
    /// JSON output for Nightscout compatibility. This is a no-op if <see cref="TimeAsSeconds"/>
    /// already has a value.
    /// </remarks>
    public void EnsureTimeAsSeconds()
    {
        if (TimeAsSeconds.HasValue) return;

        if (TimeSpan.TryParse(Time, out var timeSpan))
        {
            TimeAsSeconds = (int)timeSpan.TotalSeconds;
        }
    }
}
