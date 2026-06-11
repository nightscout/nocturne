using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Tandem.Models;

/// <summary>
/// One entry from the Tandem Source <c>pumpeventmetadata</c> endpoint, describing a pump on the
/// account and the date range for which it has events.
/// </summary>
public sealed class TandemPumpEventMetadata
{
    [JsonPropertyName("tconnectDeviceId")]
    public string TconnectDeviceId { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }

    [JsonPropertyName("modelNumber")]
    public string? ModelNumber { get; set; }

    [JsonPropertyName("minDateWithEvents")]
    public DateTimeOffset? MinDateWithEvents { get; set; }

    [JsonPropertyName("maxDateWithEvents")]
    public DateTimeOffset? MaxDateWithEvents { get; set; }

    [JsonPropertyName("softwareVersion")]
    public string? SoftwareVersion { get; set; }

    [JsonPropertyName("partNumber")]
    public string? PartNumber { get; set; }

    [JsonPropertyName("lastUpload")]
    public TandemLastUpload? LastUpload { get; set; }
}

/// <summary>The most recent upload for a pump, which carries the current pump settings snapshot.</summary>
public sealed class TandemLastUpload
{
    [JsonPropertyName("settings")]
    public TandemPumpSettings? Settings { get; set; }
}

/// <summary>Pump settings snapshot (profiles and CGM alert thresholds).</summary>
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

    [JsonPropertyName("tDependentSegs")]
    public List<TandemPumpProfileSegment> TDependentSegs { get; set; } = [];

    /// <summary>Insulin duration in minutes.</summary>
    [JsonPropertyName("insulinDuration")]
    public int InsulinDuration { get; set; }

    [JsonPropertyName("carbEntry")]
    public int CarbEntry { get; set; }

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

    [JsonPropertyName("carbRatio")]
    public int CarbRatio { get; set; }

    [JsonPropertyName("targetBg")]
    public int TargetBg { get; set; }

    /// <summary>An all-zero placeholder segment that should be ignored (matches tconnectsync's <c>skip</c>).</summary>
    [JsonIgnore]
    public bool Skip => StartTime == 0 && BasalRate == 0 && Isf == 0 && CarbRatio == 0 && TargetBg == 0;
}

public sealed class TandemPumpCgmSettings
{
    [JsonPropertyName("highGlucoseAlert")]
    public TandemGlucoseAlertSettings? HighGlucoseAlert { get; set; }

    [JsonPropertyName("lowGlucoseAlert")]
    public TandemGlucoseAlertSettings? LowGlucoseAlert { get; set; }
}

public sealed class TandemGlucoseAlertSettings
{
    [JsonPropertyName("mgPerDl")]
    public int MgPerDl { get; set; }

    [JsonPropertyName("enabled")]
    public int Enabled { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }
}
