using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Main data structure used by Nightscout clients.
/// Contains all necessary data for glucose monitoring and treatment tracking.
/// This class provides 1:1 backwards compatibility with the legacy JavaScript DData implementation.
/// </summary>
/// <seealso cref="Entry"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="Profile"/>
/// <seealso cref="Food"/>
/// <seealso cref="Activity"/>
/// <seealso cref="DDataResponse"/>
public class DData
{
    /// <summary>
    /// Glucose entries (SGV - Sensor Glucose Values)
    /// </summary>
    [JsonPropertyName("sgvs")]
    public List<Entry> Sgvs { get; set; } = new();

    [JsonPropertyName("treatments")]
    public List<Treatment> Treatments { get; set; } = new();

    /// <summary>
    /// Meter blood glucose readings
    /// </summary>
    [JsonPropertyName("mbgs")]
    public List<Entry> Mbgs { get; set; } = new();

    [JsonPropertyName("cals")]
    public List<Entry> Cals { get; set; } = new();

    /// <summary>
    /// Most recent calibration record
    /// </summary>
    [JsonPropertyName("cal")]
    public Entry? Cal { get; set; }

    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();

    [JsonPropertyName("devicestatus")]
    public List<DeviceStatus> DeviceStatus { get; set; } = new();

    /// <summary>
    /// Food database entries
    /// </summary>
    [JsonPropertyName("food")]
    public List<Food> Food { get; set; } = new();

    [JsonPropertyName("activity")]
    public List<Activity> Activity { get; set; } = new();

    [JsonPropertyName("dbstats")]
    public DbStats DbStats { get; set; } = new();

    /// <summary>
    /// Timestamp when this data was last updated (Unix milliseconds)
    /// </summary>
    [JsonPropertyName("lastUpdated")]
    public long LastUpdated { get; set; }

    /// <summary>
    /// Whether the client is in retro mode (viewing historical data)
    /// </summary>
    [JsonPropertyName("inRetroMode")]
    public bool? InRetroMode { get; set; }

    /// <summary>
    /// Site change treatments (filtered from treatments)
    /// </summary>
    [JsonPropertyName("sitechangeTreatments")]
    public List<Treatment> SiteChangeTreatments { get; set; } = new();

    /// <summary>
    /// Insulin change treatments (filtered from treatments)
    /// </summary>
    [JsonPropertyName("insulinchangeTreatments")]
    public List<Treatment> InsulinChangeTreatments { get; set; } = new();

    /// <summary>
    /// Battery change treatments (filtered from treatments)
    /// </summary>
    [JsonPropertyName("batteryTreatments")]
    public List<Treatment> BatteryTreatments { get; set; } = new();

    /// <summary>
    /// Sensor treatments (filtered from treatments)
    /// </summary>
    [JsonPropertyName("sensorTreatments")]
    public List<Treatment> SensorTreatments { get; set; } = new();

    /// <summary>
    /// Combo bolus treatments (filtered from treatments)
    /// </summary>
    [JsonPropertyName("combobolusTreatments")]
    public List<Treatment> ComboBolusTreatments { get; set; } = new();

    /// <summary>
    /// Profile switch treatments (filtered and processed from treatments)
    /// </summary>
    [JsonPropertyName("profileTreatments")]
    public List<Treatment> ProfileTreatments { get; set; } = new();

    /// <summary>
    /// Temporary basal treatments (filtered and processed from treatments)
    /// </summary>
    [JsonPropertyName("tempbasalTreatments")]
    public List<Treatment> TempBasalTreatments { get; set; } = new();

    /// <summary>
    /// Temporary target treatments (filtered and processed from treatments)
    /// </summary>
    [JsonPropertyName("tempTargetTreatments")]
    public List<Treatment> TempTargetTreatments { get; set; } = new();

    /// <summary>
    /// Profile name from the most recent zero-duration Profile Switch treatment.
    /// Used by Loop and other clients for active profile determination.
    /// </summary>
    [JsonPropertyName("lastProfileFromSwitch")]
    public string? LastProfileFromSwitch { get; set; }
}

public class DbStats
{
    [JsonPropertyName("dataSize")]
    public long DataSize { get; set; }

    [JsonPropertyName("collections")]
    public int Collections { get; set; }

    [JsonPropertyName("indexes")]
    public int Indexes { get; set; }
}

/// <summary>
/// Response structure for DData endpoints that includes recent device status
/// This matches the dataWithRecentStatuses method from the legacy implementation
/// </summary>
public class DDataResponse
{
    /// <summary>
    /// Recent device status entries
    /// </summary>
    [JsonPropertyName("devicestatus")]
    public List<DeviceStatus> DeviceStatus { get; set; } = new();

    /// <summary>
    /// Glucose entries
    /// </summary>
    [JsonPropertyName("sgvs")]
    public List<Entry> Sgvs { get; set; } = new();

    [JsonPropertyName("cals")]
    public List<Entry> Cals { get; set; } = new();

    /// <summary>
    /// Profile records (with temporary profile switches filtered out)
    /// </summary>
    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>
    /// Meter blood glucose readings
    /// </summary>
    [JsonPropertyName("mbgs")]
    public List<Entry> Mbgs { get; set; } = new();

    /// <summary>
    /// Food database entries
    /// </summary>
    [JsonPropertyName("food")]
    public List<Food> Food { get; set; } = new();

    [JsonPropertyName("treatments")]
    public List<Treatment> Treatments { get; set; } = new();

    [JsonPropertyName("dbstats")]
    public DbStats DbStats { get; set; } = new();
}
