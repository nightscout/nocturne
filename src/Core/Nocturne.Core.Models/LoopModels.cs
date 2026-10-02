using System.Text.Json.Serialization;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// Loop notification data model matching the legacy loop.js data structure.
/// Maintains 1:1 compatibility with the original Nightscout Loop implementation.
/// </summary>
/// <seealso cref="LoopConfiguration"/>
/// <seealso cref="LoopProfileSettings"/>
public class LoopNotificationData
{
    /// <summary>
    /// Event type - determines the type of Loop notification to send
    /// Valid values: "Temporary Override Cancel", "Temporary Override", "Remote Carbs Entry", "Remote Bolus Entry"
    /// </summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    /// <summary>
    /// Notes/comments associated with the notification
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Username or identifier of who entered the data
    /// </summary>
    [JsonPropertyName("enteredBy")]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Reason for temporary override (used when eventType is "Temporary Override")
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Display name for the reason (used when eventType is "Temporary Override")
    /// </summary>
    [JsonPropertyName("reasonDisplay")]
    public string? ReasonDisplay { get; set; }

    /// <summary>
    /// Duration in minutes for temporary override
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Duration { get; set; }

    /// <summary>
    /// Remote carbs amount in grams (used when eventType is "Remote Carbs Entry")
    /// </summary>
    [JsonPropertyName("remoteCarbs")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? RemoteCarbs { get; set; }

    /// <summary>
    /// Carb absorption time in hours (used when eventType is "Remote Carbs Entry")
    /// </summary>
    [JsonPropertyName("remoteAbsorption")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? RemoteAbsorption { get; set; }

    /// <summary>
    /// Remote bolus amount in units (used when eventType is "Remote Bolus Entry")
    /// </summary>
    [JsonPropertyName("remoteBolus")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? RemoteBolus { get; set; }

    /// <summary>
    /// One-time password for secure operations
    /// </summary>
    [JsonPropertyName("otp")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Otp { get; set; }

    /// <summary>
    /// Timestamp when the entry was created (ISO 8601 format)
    /// </summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }
}

/// <summary>
/// Loop configuration settings for Apple Push Notification Service integration.
/// Bound from appsettings.json section "Loop".
/// </summary>
public class LoopConfiguration
{
    /// <summary>
    /// Apple Push Notification Service (APNS) private key in PEM format
    /// </summary>
    public string? ApnsKey { get; set; }

    /// <summary>
    /// APNS Key ID (10-character string)
    /// </summary>
    public string? ApnsKeyId { get; set; }

    /// <summary>
    /// Apple Developer Team ID (10-character string)
    /// </summary>
    public string? DeveloperTeamId { get; set; }

    /// <summary>
    /// APNs server for Loop pushes. Only an explicit <c>"development"</c> selects the sandbox:
    /// TestFlight and App Store Loop builds hold production tokens, which the sandbox rejects.
    /// </summary>
    public string PushServerEnvironment { get; set; } = ConnectorDefaults.LoopPushServerEnvironment;

    /// <summary>
    /// Optional override URL for APNS server (used for testing)
    /// When set, requests are sent to this URL instead of Apple's servers
    /// </summary>
    public string? ApnsServerOverrideUrl { get; set; }
}

/// <summary>
/// Loop notification response model
/// </summary>
public class LoopNotificationResponse
{
    /// <summary>
    /// Indicates if the notification was sent successfully
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Response message or error description
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the response was generated
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// Additional response data
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// Loop override preset configuration stored in profiles
/// Used by Nightscout to display and trigger override presets
/// </summary>
public class LoopOverridePreset
{
    /// <summary>
    /// Gets or sets the preset name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the emoji symbol for the preset
    /// </summary>
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds (Loop's external format)
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// <see cref="Duration"/> converted from seconds to minutes for consistency with <see cref="Treatment.Duration"/>.
    /// Returns <see langword="null"/> if <see cref="Duration"/> is not set.
    /// </summary>
    /// <remarks>Loop stores duration in seconds externally; Nocturne uses minutes internally.</remarks>
    [JsonIgnore]
    public double? DurationMinutes => Duration.HasValue ? Duration.Value / 60.0 : null;

    /// <summary>
    /// Gets or sets the target glucose range
    /// </summary>
    [JsonPropertyName("targetRange")]
    [JsonConverter(typeof(LoopTargetRangeConverter))]
    public LoopTargetRange? TargetRange { get; set; }

    /// <summary>
    /// Gets or sets the insulin needs scale factor (e.g., 0.9 for 90%)
    /// </summary>
    [JsonPropertyName("insulinNeedsScaleFactor")]
    public double? InsulinNeedsScaleFactor { get; set; }
}

/// <summary>
/// Loop target glucose range
/// </summary>
public class LoopTargetRange
{
    /// <summary>
    /// Gets or sets the minimum target value
    /// </summary>
    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }

    /// <summary>
    /// Gets or sets the maximum target value
    /// </summary>
    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; set; }
}

/// <summary>
/// Loop settings stored in profile (loopSettings field)
/// Contains all Loop-specific configuration for Nightscout integration
/// </summary>
public class LoopProfileSettings
{
    /// <summary>
    /// Gets or sets the Apple Push Notification device token
    /// </summary>
    [JsonPropertyName("deviceToken")]
    public string? DeviceToken { get; set; }

    /// <summary>
    /// Gets or sets the iOS bundle identifier for the Loop app
    /// </summary>
    [JsonPropertyName("bundleIdentifier")]
    public string? BundleIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the list of override presets
    /// </summary>
    [JsonPropertyName("overridePresets")]
    public List<LoopOverridePreset>? OverridePresets { get; set; }

    /// <summary>
    /// Gets or sets whether closed loop dosing is enabled
    /// </summary>
    [JsonPropertyName("dosingEnabled")]
    public bool? DosingEnabled { get; set; }

    /// <summary>
    /// Gets or sets the minimum BG guard value
    /// </summary>
    [JsonPropertyName("minimumBGGuard")]
    public double? MinimumBGGuard { get; set; }

    /// <summary>
    /// Gets or sets the pre-meal target range
    /// </summary>
    [JsonPropertyName("preMealTargetRange")]
    [JsonConverter(typeof(LoopTargetRangeConverter))]
    public LoopTargetRange? PreMealTargetRange { get; set; }

    /// <summary>
    /// Gets or sets the workout target range
    /// </summary>
    [JsonPropertyName("workoutTargetRange")]
    [JsonConverter(typeof(LoopTargetRangeConverter))]
    public LoopTargetRange? WorkoutTargetRange { get; set; }

    /// <summary>
    /// Gets or sets the maximum bolus amount in units
    /// </summary>
    [JsonPropertyName("maximumBolus")]
    public double? MaximumBolus { get; set; }

    /// <summary>
    /// Gets or sets the maximum basal rate in units per hour
    /// </summary>
    [JsonPropertyName("maximumBasalRatePerHour")]
    public double? MaximumBasalRatePerHour { get; set; }

    /// <summary>
    /// Gets or sets the dosing strategy (e.g., "automaticBolus", "tempBasalOnly")
    /// </summary>
    [JsonPropertyName("dosingStrategy")]
    public string? DosingStrategy { get; set; }

    /// <summary>
    /// Gets or sets the currently active schedule override
    /// </summary>
    [JsonPropertyName("scheduleOverride")]
    public LoopOverridePreset? ScheduleOverride { get; set; }
}
