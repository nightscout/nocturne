using System.Globalization;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a Nightscout profile record for the API.
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
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the name of the default profile in the store
    /// </summary>
    [JsonPropertyName("defaultProfile")]
    public string DefaultProfile { get; set; } = "Default";

    /// <summary>
    /// Gets or sets the start date for this profile record
    /// </summary>
    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Gets or sets the time in milliseconds since the Unix epoch
    /// Nightscout may send this as either a number or a string
    /// </summary>
    [JsonPropertyName("mills")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long Mills { get; set; }

    /// <summary>
    /// Gets or sets the server-modified timestamp (Unix milliseconds). V3 compatibility field.
    /// Falls back to Mills, then StartDate (profiles are often uploaded without mills):
    /// NS v3 socket clients (AAPS) read it unconditionally from realtime storage events
    /// and drop docs without it.
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
    /// Gets or sets the server-created timestamp (Unix milliseconds). V3 compatibility field.
    /// </summary>
    private long? _srvCreated;

    [JsonPropertyName("srvCreated")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? SrvCreated
    {
        get => _srvCreated ?? FallbackTimestampMills();
        set => _srvCreated = value;
    }

    private long? FallbackTimestampMills()
    {
        if (Mills > 0)
            return Mills;
        return DateTimeOffset.TryParse(
            StartDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var startDate
        )
            ? startDate.ToUnixTimeMilliseconds()
            : null;
    }

    /// <summary>
    /// Gets or sets when this profile was created
    /// </summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the units used for blood glucose values (mg/dL or mmol/L)
    /// </summary>
    [JsonPropertyName("units")]
    public string Units { get; set; } = "mg/dL";

    /// <summary>
    /// Gets or sets the store containing all named profiles
    /// </summary>
    [JsonPropertyName("store")]
    public Dictionary<string, ProfileData> Store { get; set; } = new();

    /// <summary>
    /// Gets or sets who entered this profile (e.g., "Loop")
    /// Required for Nightscout compatibility
    /// </summary>
    [JsonPropertyName("enteredBy")]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets Loop-specific settings for this profile.
    /// This is critical for Loop compatibility - contains device tokens, dosing settings,
    /// override presets, and schedule overrides.
    /// </summary>
    [JsonPropertyName("loopSettings")]
    public LoopProfileSettings? LoopSettings { get; set; }

    /// <summary>
    /// Gets or sets whether this profile is managed by an external service (e.g. Glooko)
    /// </summary>
    [NocturneOnly]
    public bool IsExternallyManaged { get; set; }

    /// <summary>
    /// Gets or sets whether this profile was converted on the fly from legacy format
    /// </summary>
    [JsonIgnore]
    public bool ConvertedOnTheFly { get; set; }
}

/// <summary>
/// Represents the data for a specific named profile within a profile record
/// </summary>
public class ProfileData
{
    /// <summary>
    /// Gets or sets the duration of insulin action in hours
    /// </summary>
    [JsonPropertyName("dia")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Dia { get; set; } = 3.0;

    /// <summary>
    /// Gets or sets the carbs absorption rate in grams per hour
    /// </summary>
    [JsonPropertyName("carbs_hr")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int CarbsHr { get; set; } = 20;

    /// <summary>
    /// Gets or sets the carb absorption delay in minutes
    /// </summary>
    [JsonPropertyName("delay")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Delay { get; set; } = 20;

    /// <summary>
    /// Gets or sets the timezone for this profile
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>
    /// Gets or sets the units used for blood glucose values
    /// </summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    /// <summary>
    /// Gets or sets whether to use GI-specific carb values
    /// </summary>
    [JsonPropertyName("perGIvalues")]
    public bool? PerGIValues { get; set; }

    /// <summary>
    /// Gets or sets the carbs absorption rate for high GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_high")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrHigh { get; set; }

    /// <summary>
    /// Gets or sets the carbs absorption rate for medium GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_medium")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrMedium { get; set; }

    /// <summary>
    /// Gets or sets the carbs absorption rate for low GI foods
    /// </summary>
    [JsonPropertyName("carbs_hr_low")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? CarbsHrLow { get; set; }

    /// <summary>
    /// Gets or sets the delay for high GI carbs
    /// </summary>
    [JsonPropertyName("delay_high")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayHigh { get; set; }

    /// <summary>
    /// Gets or sets the delay for medium GI carbs
    /// </summary>
    [JsonPropertyName("delay_medium")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayMedium { get; set; }

    /// <summary>
    /// Gets or sets the delay for low GI carbs
    /// </summary>
    [JsonPropertyName("delay_low")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? DelayLow { get; set; }

    /// <summary>
    /// Gets or sets the basal rates throughout the day
    /// </summary>
    [JsonPropertyName("basal")]
    public List<TimeValue> Basal { get; set; } = new();

    /// <summary>
    /// Gets or sets the carb ratios throughout the day
    /// </summary>
    [JsonPropertyName("carbratio")]
    public List<TimeValue> CarbRatio { get; set; } = new();

    /// <summary>
    /// Gets or sets the insulin sensitivity factors throughout the day
    /// </summary>
    [JsonPropertyName("sens")]
    public List<TimeValue> Sens { get; set; } = new();

    /// <summary>
    /// Gets or sets the low blood glucose targets throughout the day
    /// </summary>
    [JsonPropertyName("target_low")]
    public List<TimeValue> TargetLow { get; set; } = new();

    /// <summary>
    /// Gets or sets the high blood glucose targets throughout the day
    /// </summary>
    [JsonPropertyName("target_high")]
    public List<TimeValue> TargetHigh { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this profile was converted on the fly from legacy format
    /// </summary>
    [JsonIgnore]
    public bool ConvertedOnTheFly { get; set; }
}

/// <summary>
/// Represents a time-based value used in profiles (e.g., basal rates, carb ratios)
/// </summary>
public class TimeValue
{
    /// <summary>
    /// Gets or sets the time in HH:mm format (e.g., "06:00")
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "00:00";

    /// <summary>
    /// Gets or sets the value for this time period
    /// </summary>
    [JsonPropertyName("value")]
    [JsonConverter(typeof(FlexibleDoubleConverter))]
    public double Value { get; set; }

    /// <summary>
    /// Gets or sets the time converted to seconds since midnight for faster calculations.
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
