using System.Text.Json.Serialization;

namespace Nocturne.Connectors.CareLink.Models;

public class CareLinkData
{
    [JsonPropertyName("sgs")]
    public List<CareLinkSensorGlucose>? Sgs { get; set; }

    [JsonPropertyName("lastSG")]
    public CareLinkSensorGlucose? LastSG { get; set; }

    [JsonPropertyName("lastSGTrend")]
    public string? LastSGTrend { get; set; }

    [JsonPropertyName("currentServerTime")]
    public long CurrentServerTime { get; set; }

    [JsonPropertyName("sMedicalDeviceTime")]
    public string? MedicalDeviceTime { get; set; }

    [JsonPropertyName("lastMedicalDeviceDataUpdateServerTime")]
    public long LastMedicalDeviceDataUpdateServerTime { get; set; }

    [JsonPropertyName("medicalDeviceFamily")]
    public string? MedicalDeviceFamily { get; set; }

    [JsonPropertyName("medicalDeviceBatteryLevelPercent")]
    public int? MedicalDeviceBatteryLevelPercent { get; set; }

    [JsonPropertyName("conduitBatteryLevel")]
    public int? ConduitBatteryLevel { get; set; }

    [JsonPropertyName("conduitBatteryStatus")]
    public string? ConduitBatteryStatus { get; set; }

    [JsonPropertyName("conduitInRange")]
    public bool? ConduitInRange { get; set; }

    [JsonPropertyName("conduitMedicalDeviceInRange")]
    public bool? ConduitMedicalDeviceInRange { get; set; }

    [JsonPropertyName("conduitSensorInRange")]
    public bool? ConduitSensorInRange { get; set; }

    [JsonPropertyName("sensorState")]
    public string? SensorState { get; set; }

    [JsonPropertyName("calibStatus")]
    public string? CalibStatus { get; set; }

    [JsonPropertyName("sensorDurationHours")]
    public int? SensorDurationHours { get; set; }

    [JsonPropertyName("timeToNextCalibHours")]
    public int? TimeToNextCalibHours { get; set; }

    [JsonPropertyName("reservoirRemainingUnits")]
    public double? ReservoirRemainingUnits { get; set; }

    [JsonPropertyName("reservoirAmount")]
    public double? ReservoirAmount { get; set; }

    [JsonPropertyName("activeInsulin")]
    public CareLinkActiveInsulin? ActiveInsulin { get; set; }

    [JsonPropertyName("lastAlarm")]
    public CareLinkAlarm? LastAlarm { get; set; }

    [JsonPropertyName("bgUnits")]
    public string? BgUnits { get; set; }

    [JsonPropertyName("bgunits")]
    public string? BgUnitsAlt { get; set; }

    [JsonPropertyName("timeFormat")]
    public string? TimeFormat { get; set; }

    [JsonIgnore]
    public string? EffectiveBgUnits => BgUnits ?? BgUnitsAlt;
}

public class CareLinkSensorGlucose
{
    [JsonPropertyName("sg")]
    public int Sg { get; set; }

    [JsonPropertyName("datetime")]
    public string? Datetime { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("timeChange")]
    public bool? TimeChange { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

public class CareLinkActiveInsulin
{
    [JsonPropertyName("datetime")]
    public string? Datetime { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

public class CareLinkAlarm
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("flash")]
    public bool Flash { get; set; }

    [JsonPropertyName("datetime")]
    public string? Datetime { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }
}
