using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents device status information from various devices
/// </summary>
public class DeviceStatus : ProcessableDocumentBase
{
    /// <summary>
    /// Gets or sets the MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// Gets or sets the timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    /// <summary>
    /// Gets or sets the ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Gets or sets the UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; } = 0;

    /// <summary>
    /// Gets or sets the uploader battery level (for compatibility with Nightscout flattened format)
    /// Maps to/from Uploader.Battery
    /// </summary>
    [JsonPropertyName("uploaderBattery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? UploaderBattery
    {
        get => Uploader?.Battery;
        set
        {
            if (value.HasValue)
            {
                Uploader ??= new UploaderStatus();
                Uploader.Battery = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the device name that submitted this status
    /// </summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the device is currently charging
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool? IsCharging { get; set; }

    /// <summary>
    /// Gets or sets the uploader status information
    /// </summary>
    [JsonPropertyName("uploader")]
    public UploaderStatus? Uploader { get; set; }

    /// <summary>
    /// Gets or sets the pump status information
    /// </summary>
    [JsonPropertyName("pump")]
    public PumpStatus? Pump { get; set; }

    /// <summary>
    /// Gets or sets the OpenAPS status information
    /// </summary>
    [JsonPropertyName("openaps")]
    public OpenApsStatus? OpenAps { get; set; }

    /// <summary>
    /// Gets or sets the Loop status information
    /// </summary>
    [JsonPropertyName("loop")]
    public LoopStatus? Loop { get; set; }

    /// <summary>
    /// Gets or sets the xDrip+ status information
    /// </summary>
    [JsonPropertyName("xdripjs")]
    public XDripJsStatus? XDripJs { get; set; }

    /// <summary>
    /// Gets or sets the radio adapter information
    /// </summary>
    [JsonPropertyName("radioAdapter")]
    public RadioAdapterStatus? RadioAdapter { get; set; }

    /// <summary>
    /// Gets or sets the MM Connect status information
    /// </summary>
    [JsonPropertyName("connect")]
    public object? Connect { get; set; }

    /// <summary>
    /// Gets or sets the override status information
    /// </summary>
    [JsonPropertyName("override")]
    public OverrideStatus? Override { get; set; }

    /// <summary>
    /// Gets or sets the CGM status information
    /// </summary>
    [JsonPropertyName("cgm")]
    public CgmStatus? Cgm { get; set; }

    /// <summary>
    /// Gets or sets the blood glucose meter status information
    /// </summary>
    [JsonPropertyName("meter")]
    public MeterStatus? Meter { get; set; }

    /// <summary>
    /// Gets or sets the insulin pen status information
    /// </summary>
    [JsonPropertyName("insulinPen")]
    public InsulinPenStatus? InsulinPen { get; set; }

    /// <summary>
    /// Gets or sets the MM tune (radio frequency tuning) information
    /// Used by OpenAPS to track pump radio communication signal strength
    /// </summary>
    [JsonPropertyName("mmtune")]
    public OpenApsMmTune? MmTune { get; set; }
}

/// <summary>
/// Represents uploader status information
/// </summary>
public class UploaderStatus
{
    /// <summary>
    /// Gets or sets the battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    /// <summary>
    /// Gets or sets the battery voltage
    /// </summary>
    [JsonPropertyName("batteryVoltage")]
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Gets or sets the device temperature
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Gets or sets the uploader name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the uploader type
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Represents pump status information
/// </summary>
public class PumpStatus
{
    /// <summary>
    /// Gets or sets the pump battery information
    /// </summary>
    [JsonPropertyName("battery")]
    public PumpBattery? Battery { get; set; }

    /// <summary>
    /// Gets or sets the reservoir level
    /// </summary>
    [JsonPropertyName("reservoir")]
    public double? Reservoir { get; set; }

    /// <summary>
    /// Gets or sets the pump clock time
    /// </summary>
    [JsonPropertyName("clock")]
    public string? Clock { get; set; }

    /// <summary>
    /// Gets or sets the pump status details
    /// </summary>
    [JsonPropertyName("status")]
    public PumpStatusDetails? Status { get; set; }

    /// <summary>
    /// Gets or sets the pump IOB information
    /// </summary>
    [JsonPropertyName("iob")]
    public PumpIob? Iob { get; set; }

    /// <summary>
    /// Display override for reservoir (e.g., "50+U" for Omnipod)
    /// Used when pump doesn't report exact reservoir levels
    /// </summary>
    [JsonPropertyName("reservoir_display_override")]
    public string? ReservoirDisplayOverride { get; set; }

    /// <summary>
    /// Level override for reservoir alerts
    /// </summary>
    [JsonPropertyName("reservoir_level_override")]
    public PumpAlertLevel? ReservoirLevelOverride { get; set; }

    /// <summary>
    /// Pump manufacturer (e.g., "Insulet", "Medtronic", "Tandem")
    /// </summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Pump model identifier
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Extended pump data (arbitrary key-value pairs from the device)
    /// </summary>
    [JsonPropertyName("extended")]
    public Dictionary<string, object>? Extended { get; set; }
}

/// <summary>
/// Represents pump battery information
/// </summary>
public class PumpBattery
{
    /// <summary>
    /// Gets or sets the battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    /// <summary>
    /// Gets or sets the battery voltage
    /// </summary>
    [JsonPropertyName("voltage")]
    public double? Voltage { get; set; }
}

/// <summary>
/// Represents pump status details
/// </summary>
public class PumpStatusDetails
{
    /// <summary>
    /// Gets or sets the pump status string
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets whether the pump is bolusing
    /// </summary>
    [JsonPropertyName("bolusing")]
    public bool? Bolusing { get; set; }

    /// <summary>
    /// Gets or sets whether the pump is suspended
    /// </summary>
    [JsonPropertyName("suspended")]
    public bool? Suspended { get; set; }
}

/// <summary>
/// Represents pump IOB information
/// </summary>
public class PumpIob
{
    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the bolus IOB
    /// </summary>
    [JsonPropertyName("bolusiob")]
    public double? BolusIob { get; set; }

    /// <summary>
    /// Gets or sets the basal IOB
    /// </summary>
    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    /// <summary>
    /// Gets or sets the total IOB
    /// </summary>
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }
}

/// <summary>
/// Represents OpenAPS status information
/// </summary>
public class OpenApsStatus
{
    /// <summary>
    /// Gets or sets the suggested action
    /// </summary>
    [JsonPropertyName("suggested")]
    public object? Suggested { get; set; }

    /// <summary>
    /// Gets or sets the enacted action
    /// </summary>
    [JsonPropertyName("enacted")]
    public object? Enacted { get; set; }

    /// <summary>
    /// Gets or sets the IOB information
    /// </summary>
    [JsonPropertyName("iob")]
    public object? Iob { get; set; }

    /// <summary>
    /// Gets or sets the COB (Carbs on Board) value
    /// </summary>
    [JsonPropertyName("cob")]
    public double? Cob { get; set; }
}

/// <summary>
/// Represents Loop status information
/// </summary>
public class LoopStatus
{
    /// <summary>
    /// Gets or sets the IOB information
    /// </summary>
    [JsonPropertyName("iob")]
    public LoopIob? Iob { get; set; }

    /// <summary>
    /// Gets or sets the COB (Carbs on Board) information
    /// </summary>
    [JsonPropertyName("cob")]
    public LoopCob? Cob { get; set; }

    /// <summary>
    /// Gets or sets the predicted glucose values
    /// </summary>
    [JsonPropertyName("predicted")]
    public LoopPredicted? Predicted { get; set; }

    /// <summary>
    /// Gets or sets the recommended bolus amount
    /// </summary>
    [JsonPropertyName("recommendedBolus")]
    public double? RecommendedBolus { get; set; }

    /// <summary>
    /// Gets or sets the Loop name/identifier
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the Loop version
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the recommended action
    /// </summary>
    [JsonPropertyName("recommended")]
    public object? Recommended { get; set; }

    /// <summary>
    /// Gets or sets the enacted action with details about temp basals and automatic boluses
    /// </summary>
    [JsonPropertyName("enacted")]
    public LoopEnacted? Enacted { get; set; }

    /// <summary>
    /// Gets or sets the recommended temporary basal rate
    /// </summary>
    [JsonPropertyName("recommendedTempBasal")]
    public LoopRecommendedTempBasal? RecommendedTempBasal { get; set; }

    /// <summary>
    /// Gets or sets the failure reason if Loop encountered an error
    /// </summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>
    /// Gets or sets the list of RileyLink statuses
    /// </summary>
    [JsonPropertyName("rileylinks")]
    public List<RileyLinkStatus>? RileyLinks { get; set; }

    /// <summary>
    /// Gets or sets the automatic dose recommendation
    /// </summary>
    [JsonPropertyName("automaticDoseRecommendation")]
    public LoopAutomaticDoseRecommendation? AutomaticDoseRecommendation { get; set; }

    /// <summary>
    /// Gets or sets the current correction range
    /// </summary>
    [JsonPropertyName("currentCorrectionRange")]
    public CorrectionRange? CurrentCorrectionRange { get; set; }

    /// <summary>
    /// Gets or sets the forecast error (prediction error info)
    /// </summary>
    [JsonPropertyName("forecastError")]
    public object? ForecastError { get; set; }
}

/// <summary>
/// Represents RileyLink status information
/// </summary>
public class RileyLinkStatus
{
    /// <summary>
    /// Gets or sets the name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the signal strength
    /// </summary>
    [JsonPropertyName("signal")]
    public double? Signal { get; set; }

    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the radio firmware version
    /// </summary>
    [JsonPropertyName("radioFirmware")]
    public string? RadioFirmware { get; set; }

    /// <summary>
    /// Gets or sets the BLE firmware version
    /// </summary>
    [JsonPropertyName("bleFirmware")]
    public string? BleFirmware { get; set; }

    /// <summary>
    /// Gets or sets whether connected
    /// </summary>
    [JsonPropertyName("connected")]
    public bool? Connected { get; set; }
}

/// <summary>
/// Represents Loop automatic dose recommendation
/// </summary>
public class LoopAutomaticDoseRecommendation
{
    /// <summary>
    /// Gets or sets the recommended bolus
    /// </summary>
    [JsonPropertyName("bolus")]
    public double? Bolus { get; set; }

    /// <summary>
    /// Gets or sets the recommended temp basal
    /// </summary>
    [JsonPropertyName("tempBasal")]
    public LoopRecommendedTempBasal? TempBasal { get; set; }

    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the recommendation notice/reason
    /// </summary>
    [JsonPropertyName("notice")]
    public string? Notice { get; set; }
}

/// <summary>
/// Represents Loop COB (Carbs on Board) information
/// </summary>
public class LoopCob
{
    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the COB value
    /// </summary>
    [JsonPropertyName("cob")]
    public double? Cob { get; set; }
}

/// <summary>
/// Represents Loop predicted glucose values
/// </summary>
public class LoopPredicted
{
    /// <summary>
    /// Gets or sets the predicted glucose values
    /// </summary>
    [JsonPropertyName("values")]
    public double[]? Values { get; set; }

    /// <summary>
    /// Gets or sets the start date of the prediction
    /// </summary>
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }
}

/// <summary>
/// Represents Loop IOB information
/// </summary>
public class LoopIob
{
    /// <summary>
    /// Gets or sets the timestamp
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the IOB value
    /// </summary>
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }

    /// <summary>
    /// Gets or sets the basal IOB component
    /// </summary>
    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    /// <summary>
    /// Gets or sets the net basal insulin
    /// </summary>
    [JsonPropertyName("netbasalinsulin")]
    public double? NetBasalInsulin { get; set; }
}

/// <summary>
/// Represents Loop enacted action details (temp basals and automatic boluses)
/// </summary>
public class LoopEnacted
{
    /// <summary>
    /// Gets or sets the automatic bolus volume in units
    /// </summary>
    [JsonPropertyName("bolusVolume")]
    public double? BolusVolume { get; set; }

    /// <summary>
    /// Gets or sets the temp basal rate in U/hr
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Gets or sets the temp basal duration in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the enacted action
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the reason for the enacted action
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets whether the pump received the command
    /// </summary>
    [JsonPropertyName("received")]
    public bool? Received { get; set; }
}

/// <summary>
/// Represents Loop recommended temporary basal rate
/// </summary>
public class LoopRecommendedTempBasal
{
    /// <summary>
    /// Gets or sets the recommended rate in U/hr
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Gets or sets the recommended duration in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the recommendation
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

/// <summary>
/// Represents xDrip+ status information
/// </summary>
public class XDripJsStatus
{
    /// <summary>
    /// Gets or sets the state
    /// </summary>
    [JsonPropertyName("state")]
    public int? State { get; set; }

    /// <summary>
    /// Gets or sets the state string
    /// </summary>
    [JsonPropertyName("stateString")]
    public string? StateString { get; set; }

    /// <summary>
    /// Gets or sets voltage A
    /// </summary>
    [JsonPropertyName("voltagea")]
    public double? VoltageA { get; set; }

    /// <summary>
    /// Gets or sets voltage B
    /// </summary>
    [JsonPropertyName("voltageb")]
    public double? VoltageB { get; set; }
}

/// <summary>
/// Represents radio adapter status information
/// </summary>
public class RadioAdapterStatus
{
    /// <summary>
    /// Gets or sets the pump RSSI
    /// </summary>
    [JsonPropertyName("pumpRSSI")]
    public int? PumpRssi { get; set; }

    /// <summary>
    /// Gets or sets the RSSI
    /// </summary>
    [JsonPropertyName("RSSI")]
    public int? Rssi { get; set; }
}

/// <summary>
/// Represents override status information
/// </summary>
public class OverrideStatus
{
    /// <summary>
    /// Gets or sets the override name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the timestamp as ISO 8601 string
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the duration
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Gets or sets whether the override is active
    /// </summary>
    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    /// <summary>
    /// Gets or sets the multiplier
    /// </summary>
    [JsonPropertyName("multiplier")]
    public double? Multiplier { get; set; }

    /// <summary>
    /// Gets or sets the current correction range
    /// </summary>
    [JsonPropertyName("currentCorrectionRange")]
    public CorrectionRange? CurrentCorrectionRange { get; set; }
}

/// <summary>
/// Represents correction range information
/// </summary>
public class CorrectionRange
{
    /// <summary>
    /// Gets or sets the maximum value
    /// </summary>
    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; set; }

    /// <summary>
    /// Gets or sets the minimum value
    /// </summary>
    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }
}

/// <summary>
/// Represents CGM device status information
/// </summary>
public class CgmStatus
{
    /// <summary>
    /// Gets or sets the sensor age in human-readable format (e.g., "1d 4h 23m")
    /// </summary>
    [JsonPropertyName("sensorAge")]
    public string? SensorAge { get; set; }

    /// <summary>
    /// Gets or sets the transmitter age in human-readable format
    /// </summary>
    [JsonPropertyName("transmitterAge")]
    public string? TransmitterAge { get; set; }

    /// <summary>
    /// Gets or sets the signal strength percentage (0-100)
    /// </summary>
    [JsonPropertyName("signalStrength")]
    public double? SignalStrength { get; set; }

    /// <summary>
    /// Gets or sets the calibration status
    /// </summary>
    [JsonPropertyName("calibrationStatus")]
    public string? CalibrationStatus { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last reading
    /// </summary>
    [JsonPropertyName("lastReading")]
    public DateTime? LastReading { get; set; }

    /// <summary>
    /// Gets or sets the sensor state
    /// </summary>
    [JsonPropertyName("sensorState")]
    public string? SensorState { get; set; }

    /// <summary>
    /// Gets or sets the sensor session time remaining in minutes
    /// </summary>
    [JsonPropertyName("sessionTimeRemaining")]
    public int? SessionTimeRemaining { get; set; }

    /// <summary>
    /// Gets or sets the transmitter battery level
    /// </summary>
    [JsonPropertyName("transmitterBattery")]
    public int? TransmitterBattery { get; set; }
}

/// <summary>
/// Represents blood glucose meter status information
/// </summary>
public class MeterStatus
{
    /// <summary>
    /// Gets or sets the battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// Gets or sets the number of test strips remaining
    /// </summary>
    [JsonPropertyName("testStripsRemaining")]
    public int? TestStripsRemaining { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last reading
    /// </summary>
    [JsonPropertyName("lastReading")]
    public DateTime? LastReading { get; set; }

    /// <summary>
    /// Gets or sets the clock status
    /// </summary>
    [JsonPropertyName("clockStatus")]
    public string? ClockStatus { get; set; }

    /// <summary>
    /// Gets or sets the meter model
    /// </summary>
    [JsonPropertyName("meterModel")]
    public string? MeterModel { get; set; }

    /// <summary>
    /// Gets or sets the memory usage percentage
    /// </summary>
    [JsonPropertyName("memoryUsage")]
    public double? MemoryUsage { get; set; }
}

/// <summary>
/// Represents smart insulin pen status information
/// </summary>
public class InsulinPenStatus
{
    /// <summary>
    /// Gets or sets the battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// Gets or sets the remaining insulin units in the cartridge
    /// </summary>
    [JsonPropertyName("cartridgeRemaining")]
    public double? CartridgeRemaining { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last injection
    /// </summary>
    [JsonPropertyName("lastInjection")]
    public DateTime? LastInjection { get; set; }

    /// <summary>
    /// Gets or sets the pen model
    /// </summary>
    [JsonPropertyName("penModel")]
    public string? PenModel { get; set; }

    /// <summary>
    /// Gets or sets the insulin type in the pen
    /// </summary>
    [JsonPropertyName("insulinType")]
    public string? InsulinType { get; set; }

    /// <summary>
    /// Gets or sets the needle attachment status
    /// </summary>
    [JsonPropertyName("needleAttached")]
    public bool? NeedleAttached { get; set; }

    /// <summary>
    /// Gets or sets the cartridge expiration date
    /// </summary>
    [JsonPropertyName("cartridgeExpiration")]
    public DateTime? CartridgeExpiration { get; set; }
}
