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

    [JsonPropertyName("pumpModelNumber")]
    public string? PumpModelNumber { get; set; }

    [JsonPropertyName("medicalDeviceSerialNumber")]
    public string? MedicalDeviceSerialNumber { get; set; }

    [JsonPropertyName("cgmInfo")]
    public CareLinkCgmInfo? CgmInfo { get; set; }

    [JsonPropertyName("medicalDeviceInformation")]
    public CareLinkMedicalDeviceInformation? MedicalDeviceInformation { get; set; }

    [JsonPropertyName("medicalDeviceSuspended")]
    public bool? MedicalDeviceSuspended { get; set; }

    [JsonPropertyName("gstBatteryLevel")]
    public int? GstBatteryLevel { get; set; }

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

    /// <summary>
    /// CareLink is inconsistent about the casing of this key ("bgUnits" on some endpoints,
    /// "bgunits" on others). The connector deserializes case-insensitively, so this one property
    /// binds either spelling — a second aliased property would collide on the same name and throw
    /// when the type's metadata is built, before any response is read.
    /// </summary>
    [JsonPropertyName("bgUnits")]
    public string? BgUnits { get; set; }

    [JsonPropertyName("timeFormat")]
    public string? TimeFormat { get; set; }

    [JsonPropertyName("markers")]
    public List<CareLinkMarker>? Markers { get; set; }

    [JsonPropertyName("therapyAlgorithmState")]
    public CareLinkTherapyAlgorithmState? TherapyAlgorithmState { get; set; }

    [JsonPropertyName("notificationHistory")]
    public CareLinkNotificationHistory? NotificationHistory { get; set; }

    [JsonPropertyName("clientTimeZoneName")]
    public string? ClientTimeZoneName { get; set; }
}

/// <summary>
/// A single periodic-payload marker. One permissive type covers every observed marker variant
/// (INSULIN, MEAL, AUTO_BASAL_DELIVERY, AUTO_MODE_STATUS, LOW_GLUCOSE_SUSPENDED); only the fields
/// relevant to a given <see cref="Type"/> are populated.
/// </summary>
public class CareLinkMarker
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("dateTime")]
    public string? DateTime { get; set; }

    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    // INSULIN
    [JsonPropertyName("bolusType")]
    public string? BolusType { get; set; }

    [JsonPropertyName("activationType")]
    public string? ActivationType { get; set; }

    [JsonPropertyName("programmedFastAmount")]
    public double? ProgrammedFastAmount { get; set; }

    [JsonPropertyName("deliveredFastAmount")]
    public double? DeliveredFastAmount { get; set; }

    [JsonPropertyName("programmedExtendedAmount")]
    public double? ProgrammedExtendedAmount { get; set; }

    [JsonPropertyName("deliveredExtendedAmount")]
    public double? DeliveredExtendedAmount { get; set; }

    [JsonPropertyName("programmedDuration")]
    public int? ProgrammedDuration { get; set; }

    [JsonPropertyName("effectiveDuration")]
    public int? EffectiveDuration { get; set; }

    [JsonPropertyName("completed")]
    public bool? Completed { get; set; }

    // MEAL
    [JsonPropertyName("amount")]
    public double? Amount { get; set; }

    // AUTO_BASAL_DELIVERY
    [JsonPropertyName("bolusAmount")]
    public double? BolusAmount { get; set; }

    // AUTO_MODE_STATUS
    [JsonPropertyName("autoModeOn")]
    public bool? AutoModeOn { get; set; }

    // LOW_GLUCOSE_SUSPENDED
    [JsonPropertyName("deliverySuspended")]
    public bool? DeliverySuspended { get; set; }
}

/// <summary>
/// The pump's current closed-loop algorithm state. <see cref="AutoModeShieldState"/> drives the
/// Automatic/Manual pump-mode signal.
/// </summary>
public class CareLinkTherapyAlgorithmState
{
    [JsonPropertyName("autoModeShieldState")]
    public string? AutoModeShieldState { get; set; }

    [JsonPropertyName("autoModeReadinessState")]
    public string? AutoModeReadinessState { get; set; }

    [JsonPropertyName("plgmLgsState")]
    public string? PlgmLgsState { get; set; }

    [JsonPropertyName("safeBasalDuration")]
    public int? SafeBasalDuration { get; set; }
}

public class CareLinkNotificationHistory
{
    [JsonPropertyName("activeNotifications")]
    public List<CareLinkNotification>? ActiveNotifications { get; set; }

    [JsonPropertyName("clearedNotifications")]
    public List<CareLinkNotification>? ClearedNotifications { get; set; }
}

public class CareLinkNotification
{
    [JsonPropertyName("referenceGUID")]
    public string? ReferenceGUID { get; set; }

    [JsonPropertyName("dateTime")]
    public string? DateTime { get; set; }

    [JsonPropertyName("triggeredDateTime")]
    public string? TriggeredDateTime { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("faultId")]
    public int? FaultId { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("unitsRemaining")]
    public double? UnitsRemaining { get; set; }
}

public class CareLinkCgmInfo
{
    [JsonPropertyName("sensorType")]
    public string? SensorType { get; set; }
}

public class CareLinkMedicalDeviceInformation
{
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("modelNumber")]
    public string? ModelNumber { get; set; }

    [JsonPropertyName("hardwareRevision")]
    public string? HardwareRevision { get; set; }

    [JsonPropertyName("firmwareRevision")]
    public string? FirmwareRevision { get; set; }

    [JsonPropertyName("softwareRevision")]
    public string? SoftwareRevision { get; set; }
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
