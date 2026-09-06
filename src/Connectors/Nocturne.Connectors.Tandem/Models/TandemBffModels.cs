using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tandem.Models;

/// <summary>
/// Response of <c>GET api/reports/bff/pumper/{pumperId}</c> — the pumper profile plus the pumps on
/// the account. Replaces the retired <c>reportsfacade</c> pump-event-metadata endpoint (tconnectsync
/// v3's <c>BffPumper</c>); only the fields the connector consumes are declared.
/// </summary>
public sealed class TandemBffPumper
{
    [JsonPropertyName("pumps")]
    public List<TandemBffPump> Pumps { get; set; } = [];
}

/// <summary>
/// One pump on the account (tconnectsync's <c>BffPump</c>). <see cref="AssignmentId"/> is the UUID
/// device id used as the path segment of the pump-logs endpoint (replaces the old numeric
/// <c>tconnectDeviceId</c>). Date fields are the pump's <b>local wall-clock time with no timezone</b>
/// and are null for a pump that has never uploaded.
/// </summary>
public sealed class TandemBffPump
{
    [JsonPropertyName("assignmentId")]
    public string AssignmentId { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("modelNumber")]
    public string? ModelNumber { get; set; }

    [JsonPropertyName("softwareVersion")]
    public string? SoftwareVersion { get; set; }

    [JsonPropertyName("partNumber")]
    public string? PartNumber { get; set; }

    /// <summary>Timestamp of the pump's newest event, naive pump-local ISO-8601 (no tz), or null.</summary>
    [JsonPropertyName("maxDateOfEvents")]
    public string? MaxDateOfEvents { get; set; }

    [JsonPropertyName("availableDataRange")]
    public TandemBffAvailableDataRange? AvailableDataRange { get; set; }

    [JsonPropertyName("settings")]
    public TandemBffSettingsEnvelope? Settings { get; set; }
}

/// <summary>Date range for which the pump has events; naive pump-local ISO-8601, null when never uploaded.</summary>
public sealed class TandemBffAvailableDataRange
{
    [JsonPropertyName("start")]
    public string? Start { get; set; }

    [JsonPropertyName("end")]
    public string? End { get; set; }
}

/// <summary>Envelope around a pump's settings snapshot; <c>details</c> carries the settings blob.</summary>
public sealed class TandemBffSettingsEnvelope
{
    [JsonPropertyName("details")]
    public TandemPumpSettings? Details { get; set; }
}

/// <summary>Pump settings snapshot (profiles and CGM alert thresholds), from <c>settings.details</c>.</summary>
public sealed class TandemPumpSettings
{
    [JsonPropertyName("profiles")]
    public TandemPumpProfiles? Profiles { get; set; }

    [JsonPropertyName("cgmSettings")]
    public TandemPumpCgmSettings? CgmSettings { get; set; }
}

public sealed class TandemPumpProfiles
{
    [JsonPropertyName("activeIdp")]
    public int ActiveIdp { get; set; }

    [JsonPropertyName("profile")]
    public List<TandemPumpProfile> Profile { get; set; } = [];
}

public sealed class TandemPumpProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("idp")]
    public int Idp { get; set; }

    /// <summary>Profile segments; the BFF renamed the pre-BFF <c>tDependentSegs</c> key.</summary>
    [JsonPropertyName("timeDependentSegments")]
    public List<TandemPumpProfileSegment> TDependentSegs { get; set; } = [];

    /// <summary>Insulin duration in minutes.</summary>
    [JsonPropertyName("insulinDuration")]
    public int InsulinDuration { get; set; }

    /// <summary>Carb entry mode, e.g. "UnitsAsCarbs" (an int 1/0 before the BFF migration).</summary>
    [JsonPropertyName("carbEntry")]
    public string? CarbEntry { get; set; }

    /// <summary>Maximum bolus in milliunits.</summary>
    [JsonPropertyName("maxBolus")]
    public int MaxBolus { get; set; }
}

public sealed class TandemPumpProfileSegment
{
    /// <summary>Segment start time in minutes since midnight.</summary>
    [JsonPropertyName("startTime")]
    public int StartTime { get; set; }

    /// <summary>Basal rate in milliunits/hour.</summary>
    [JsonPropertyName("basalRate")]
    public int BasalRate { get; set; }

    [JsonPropertyName("isf")]
    public int Isf { get; set; }

    /// <summary>Carb ratio in milliunits.</summary>
    [JsonPropertyName("carbRatio")]
    public int CarbRatio { get; set; }

    [JsonPropertyName("targetBg")]
    public int TargetBg { get; set; }

    /// <summary>An all-zero placeholder segment that should be ignored (matches tconnectsync's <c>skip</c>).</summary>
    [JsonIgnore]
    public bool Skip => StartTime == 0 && BasalRate == 0 && Isf == 0 && CarbRatio == 0 && TargetBg == 0;
}

/// <summary>
/// CGM alert thresholds. The BFF <c>cgmSettings</c> block is flat (no nested per-alert object as in
/// the pre-BFF shape).
/// </summary>
public sealed class TandemPumpCgmSettings
{
    [JsonPropertyName("highGlucoseAlertMgPerDl")]
    public int HighGlucoseAlertMgPerDl { get; set; }

    [JsonPropertyName("lowGlucoseAlertMgPerDl")]
    public int LowGlucoseAlertMgPerDl { get; set; }
}
