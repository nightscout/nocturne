using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.Serializers;

namespace Nocturne.Core.Models;

/// <summary>
/// A device status upload from pumps, CGMs and AID systems.
/// </summary>
/// <remarks>
/// Device status records are submitted by diabetes management apps (Loop, OpenAPS/AAPS, xDrip+)
/// and contain nested status objects for the specific systems they manage.
/// The <see cref="UploaderBattery"/> property is a computed convenience alias for <see cref="UploaderStatus.Battery"/>.
/// Unknown top-level keys (e.g. AAPS <c>configuration</c>) are captured in <see cref="ExtensionData"/>.
/// </remarks>
/// <seealso cref="ProcessableDocumentBase"/>
/// <seealso cref="Entry"/>
/// <seealso cref="Treatment"/>
/// <seealso cref="OpenApsStatus"/>
/// <seealso cref="LoopStatus"/>
/// <seealso cref="PumpStatus"/>
public class DeviceStatus : ProcessableDocumentBase
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    [JsonConverter(typeof(ObjectIdJsonConverter))]
    public override string? Id { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills { get; set; }

    private long? _date;

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch. AAPS sends "date" instead of "mills".
    /// Falls back to Mills only — never to <c>created_at</c>, because DeviceStatusDecomposer
    /// seeds Mills from this property when a doc arrives without one, and a deeper fallback
    /// here would pre-empt the richer precedence in its ResolveTimestamp.
    /// </summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Date
    {
        get => _date ?? V3Timestamps.Resolve(Mills);
        set => _date = value;
    }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Server-modified timestamp (Unix milliseconds). V3 compatibility field.
    /// Falls back to <see cref="FallbackTimestampMills"/>; see <see cref="V3Timestamps"/> for
    /// why it may never serialize as null.
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

    /// <summary>
    /// <c>false</c> on a deleted document, which a v3 history read still returns so a syncing client
    /// learns of the delete; unset on a live one, as Nightscout leaves it.
    /// </summary>
    [JsonPropertyName("isValid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsValid { get; set; }

    /// <summary>
    /// Resolves the V3 compatibility timestamps: Mills, then <c>date</c>, then <c>created_at</c>.
    /// Loop and xDrip+ upload a devicestatus carrying neither <c>mills</c> nor <c>date</c>, and
    /// the broadcast document is the raw upload, so without the <c>created_at</c> leg those
    /// fields serialize as null. Reads <see cref="_date"/> rather than <see cref="Date"/> so
    /// deepening that property can never make this recurse.
    /// </summary>
    private long? FallbackTimestampMills() =>
        V3Timestamps.Resolve(Mills != 0 ? Mills : _date, CreatedAt);

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; } = 0;

    /// <summary>
    /// Uploader battery level (for compatibility with Nightscout flattened format)
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

    [JsonPropertyName("device")]
    [Sanitizable]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// The connector the status was stored for, set when its creation is announced so a write-back can
    /// tell it from an upload. Never serialized, so a client cannot set it and no response carries it.
    /// </summary>
    [JsonIgnore]
    public string? DataSource { get; set; }

    [JsonPropertyName("isCharging")]
    public bool? IsCharging { get; set; }

    [JsonPropertyName("uploader")]
    public UploaderStatus? Uploader { get; set; }

    [JsonPropertyName("pump")]
    public PumpStatus? Pump { get; set; }

    [JsonPropertyName("openaps")]
    public OpenApsStatus? OpenAps { get; set; }

    [JsonPropertyName("loop")]
    public LoopStatus? Loop { get; set; }

    [JsonPropertyName("xdripjs")]
    public XDripJsStatus? XDripJs { get; set; }

    [JsonPropertyName("radioAdapter")]
    public RadioAdapterStatus? RadioAdapter { get; set; }

    /// <summary>
    /// MM Connect status information
    /// </summary>
    [JsonPropertyName("connect")]
    public object? Connect { get; set; }

    [JsonPropertyName("override")]
    public OverrideStatus? Override { get; set; }

    [JsonPropertyName("cgm")]
    public CgmStatus? Cgm { get; set; }

    /// <summary>
    /// Blood glucose meter status information
    /// </summary>
    [JsonPropertyName("meter")]
    public MeterStatus? Meter { get; set; }

    [JsonPropertyName("insulinPen")]
    public InsulinPenStatus? InsulinPen { get; set; }

    /// <summary>
    /// MM tune (radio frequency tuning) information
    /// Used by OpenAPS to track pump radio communication signal strength
    /// </summary>
    [JsonPropertyName("mmtune")]
    public OpenApsMmTune? MmTune { get; set; }

    /// <summary>
    /// Captures unknown top-level JSON keys not mapped to typed properties (e.g. AAPS <c>configuration</c>).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class UploaderStatus
{
    /// <summary>
    /// Battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    [JsonPropertyName("batteryVoltage")]
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Whether the uploader device is charging.
    /// Trio nests this in the uploader object; xDrip-style uploaders send it top-level.
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool? IsCharging { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class PumpStatus
{
    [JsonPropertyName("battery")]
    public PumpBattery? Battery { get; set; }

    [JsonPropertyName("reservoir")]
    public double? Reservoir { get; set; }

    [JsonPropertyName("clock")]
    public string? Clock { get; set; }

    [JsonPropertyName("status")]
    public PumpStatusDetails? Status { get; set; }

    [JsonPropertyName("iob")]
    public PumpIob? Iob { get; set; }

    /// <summary>
    /// Display override for reservoir (e.g., "50+U" for Omnipod)
    /// Used when pump doesn't report exact reservoir levels
    /// </summary>
    [JsonPropertyName("reservoir_display_override")]
    public string? ReservoirDisplayOverride { get; set; }

    /// <summary>
    /// Pump manufacturer (e.g., "Insulet", "Medtronic", "Tandem")
    /// </summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Pump serial number, used to uniquely identify the device in the registry.
    /// </summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    /// <summary>
    /// Canonical closed-loop operating mode of the pump as a <see cref="PumpModeState"/> name
    /// (e.g. "Automatic", "Manual"). Connectors that can observe the pump's auto-mode state set this
    /// so the decomposer can emit <see cref="StateSpanCategory.PumpMode"/> spans. Null when the
    /// uploader/connector reports no mode signal.
    /// </summary>
    [JsonPropertyName("pumpMode")]
    public string? PumpMode { get; set; }

    /// <summary>
    /// Extended pump data (arbitrary key-value pairs from the device)
    /// </summary>
    [JsonPropertyName("extended")]
    public Dictionary<string, object>? Extended { get; set; }
}

public class PumpBattery
{
    /// <summary>
    /// Battery percentage (0-100)
    /// </summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    [JsonPropertyName("voltage")]
    public double? Voltage { get; set; }
}

public class PumpStatusDetails
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("bolusing")]
    public bool? Bolusing { get; set; }

    [JsonPropertyName("suspended")]
    public bool? Suspended { get; set; }
}

public class PumpIob
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("bolusiob")]
    public double? BolusIob { get; set; }

    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    /// <summary>
    /// Total IOB
    /// </summary>
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }
}

public class OpenApsStatus
{
    /// <summary>
    /// Suggested action from the APS algorithm
    /// </summary>
    [JsonPropertyName("suggested")]
    public OpenApsSuggested? Suggested { get; set; }

    /// <summary>
    /// Enacted action (confirmed delivered to pump)
    /// </summary>
    [JsonPropertyName("enacted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenApsEnacted? Enacted { get; set; }

    /// <summary>
    /// IOB information.
    /// Uses a custom converter to handle legacy array format (takes first element).
    /// </summary>
    [JsonPropertyName("iob")]
    [JsonConverter(typeof(OpenApsIobDataConverter))]
    public OpenApsIobData? Iob { get; set; }

    /// <summary>
    /// COB (Carbs on Board) value
    /// </summary>
    [JsonPropertyName("cob")]
    public double? Cob { get; set; }

    /// <summary>
    /// Version string (present in Trio/FreeAPS X payloads, absent in vanilla OpenAPS and AAPS)
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// OpenAPS/AAPS suggested action, with its prediction curves.
/// </summary>
public class OpenApsSuggested
{
    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }

    [JsonPropertyName("runningDynamicIsf")]
    public bool? RunningDynamicIsf { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("mills")]
    public long? Mills { get; set; }

    [JsonPropertyName("bg")]
    public double? Bg { get; set; }

    [JsonPropertyName("tick")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Tick { get; set; }

    [JsonPropertyName("eventualBG")]
    public double? EventualBG { get; set; }

    [JsonPropertyName("targetBG")]
    public double? TargetBG { get; set; }

    [JsonPropertyName("insulinReq")]
    public double? InsulinReq { get; set; }

    [JsonPropertyName("units")]
    public double? Units { get; set; }

    [JsonPropertyName("deliverAt")]
    public string? DeliverAt { get; set; }

    [JsonPropertyName("sensitivityRatio")]
    public double? SensitivityRatio { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    [JsonPropertyName("predBGs")]
    public OpenApsPredBGs? PredBGs { get; set; }

    [JsonPropertyName("COB")]
    public double? COB { get; set; }

    [JsonPropertyName("IOB")]
    public double? IOB { get; set; }

    [JsonPropertyName("variable_sens")]
    public double? VariableSens { get; set; }

    [JsonPropertyName("isfMgdlForCarbs")]
    public double? IsfMgdlForCarbs { get; set; }

    [JsonPropertyName("carbsReq")]
    public int? CarbsReq { get; set; }

    [JsonPropertyName("carbsReqWithin")]
    public int? CarbsReqWithin { get; set; }

    [JsonPropertyName("mealAssist")]
    public string? MealAssist { get; set; }

    [JsonPropertyName("consoleLog")]
    public List<string>? ConsoleLog { get; set; }

    [JsonPropertyName("consoleError")]
    public List<string>? ConsoleError { get; set; }
}

/// <summary>
/// OpenAPS/AAPS enacted action: a suggestion plus confirmation that the pump delivered it.
/// </summary>
public class OpenApsEnacted : OpenApsSuggested
{
    [JsonPropertyName("received")]
    public bool? Received { get; set; }

    [JsonPropertyName("recieved")]
    public bool? Recieved { get; set; }

    [JsonPropertyName("requested")]
    public OpenApsRequested? Requested { get; set; }

    [JsonPropertyName("smb")]
    public double? Smb { get; set; }
}

/// <summary>
/// OpenAPS requested temp basal or SMB.
/// </summary>
public class OpenApsRequested
{
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    [JsonPropertyName("temp")]
    public string? Temp { get; set; }

    [JsonPropertyName("smb")]
    public double? Smb { get; set; }
}

/// <summary>
/// Prediction curves from OpenAPS/AAPS determine-basal algorithm
/// </summary>
public class OpenApsPredBGs
{
    [JsonPropertyName("IOB")]
    public List<double?>? IOB { get; set; }

    [JsonPropertyName("ZT")]
    public List<double?>? ZT { get; set; }

    [JsonPropertyName("COB")]
    public List<double?>? COB { get; set; }

    [JsonPropertyName("UAM")]
    public List<double?>? UAM { get; set; }
}

public class OpenApsIobData
{
    [JsonPropertyName("iob")]
    public double? Iob { get; set; }

    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    [JsonPropertyName("activity")]
    public double? Activity { get; set; }

    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("mills")]
    public long? Mills { get; set; }

    [JsonPropertyName("bolusiob")]
    public double? BolusIob { get; set; }
}

/// <summary>
/// Handles legacy OpenAPS IOB format where IOB can be either an object or an array (takes first element).
/// </summary>
public class OpenApsIobDataConverter : JsonConverter<OpenApsIobData?>
{
    public override OpenApsIobData? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() > 0)
                return JsonSerializer.Deserialize<OpenApsIobData>(arr[0].GetRawText(), options);
            return null;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return JsonSerializer.Deserialize<OpenApsIobData>(doc.RootElement.GetRawText(), options);
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, OpenApsIobData? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}

public class LoopStatus
{
    [JsonPropertyName("iob")]
    public LoopIob? Iob { get; set; }

    /// <summary>
    /// COB (Carbs on Board) information
    /// </summary>
    [JsonPropertyName("cob")]
    public LoopCob? Cob { get; set; }

    [JsonPropertyName("predicted")]
    public LoopPredicted? Predicted { get; set; }

    [JsonPropertyName("recommendedBolus")]
    public double? RecommendedBolus { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("recommended")]
    public object? Recommended { get; set; }

    /// <summary>
    /// Enacted action with details about temp basals and automatic boluses
    /// </summary>
    [JsonPropertyName("enacted")]
    public LoopEnacted? Enacted { get; set; }

    /// <summary>
    /// Recommended temporary basal rate
    /// </summary>
    [JsonPropertyName("recommendedTempBasal")]
    public LoopRecommendedTempBasal? RecommendedTempBasal { get; set; }

    /// <summary>
    /// Failure reason if Loop encountered an error
    /// </summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("rileylinks")]
    public List<RileyLinkStatus>? RileyLinks { get; set; }

    [JsonPropertyName("automaticDoseRecommendation")]
    public LoopAutomaticDoseRecommendation? AutomaticDoseRecommendation { get; set; }

    [JsonPropertyName("currentCorrectionRange")]
    public CorrectionRange? CurrentCorrectionRange { get; set; }

    [JsonPropertyName("forecastError")]
    public object? ForecastError { get; set; }
}

public class RileyLinkStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("signal")]
    public double? Signal { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("radioFirmware")]
    public string? RadioFirmware { get; set; }

    [JsonPropertyName("bleFirmware")]
    public string? BleFirmware { get; set; }

    [JsonPropertyName("connected")]
    public bool? Connected { get; set; }
}

public class LoopAutomaticDoseRecommendation
{
    [JsonPropertyName("bolus")]
    public double? Bolus { get; set; }

    [JsonPropertyName("tempBasal")]
    public LoopRecommendedTempBasal? TempBasal { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("notice")]
    public string? Notice { get; set; }
}

/// <summary>Loop's carbs on board (COB).</summary>
public class LoopCob
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("cob")]
    public double? Cob { get; set; }
}

public class LoopPredicted
{
    [JsonPropertyName("values")]
    public double[]? Values { get; set; }

    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }
}

public class LoopIob
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("iob")]
    public double? Iob { get; set; }

    [JsonPropertyName("basaliob")]
    public double? BasalIob { get; set; }

    [JsonPropertyName("netbasalinsulin")]
    public double? NetBasalInsulin { get; set; }
}

/// <summary>
/// Temp basals and automatic boluses Loop has enacted.
/// </summary>
public class LoopEnacted
{
    /// <summary>
    /// Automatic bolus volume in units
    /// </summary>
    [JsonPropertyName("bolusVolume")]
    public double? BolusVolume { get; set; }

    /// <summary>
    /// Temp basal rate in U/hr
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Temp basal duration in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Whether the pump received the command
    /// </summary>
    [JsonPropertyName("received")]
    public bool? Received { get; set; }
}

public class LoopRecommendedTempBasal
{
    /// <summary>
    /// Recommended rate in U/hr
    /// </summary>
    [JsonPropertyName("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Recommended duration in minutes
    /// </summary>
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public class XDripJsStatus
{
    [JsonPropertyName("state")]
    public int? State { get; set; }

    [JsonPropertyName("stateString")]
    public string? StateString { get; set; }

    [JsonPropertyName("voltagea")]
    public double? VoltageA { get; set; }

    [JsonPropertyName("voltageb")]
    public double? VoltageB { get; set; }
}

public class RadioAdapterStatus
{
    [JsonPropertyName("pumpRSSI")]
    public int? PumpRssi { get; set; }

    [JsonPropertyName("RSSI")]
    public int? Rssi { get; set; }
}

public class OverrideStatus
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Timestamp as ISO 8601 string
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>
    /// Seconds left from <see cref="Timestamp"/>; absent when the override is indefinite. Override treatments use minutes.
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("multiplier")]
    public double? Multiplier { get; set; }

    [JsonPropertyName("currentCorrectionRange")]
    public CorrectionRange? CurrentCorrectionRange { get; set; }
}

public class CorrectionRange
{
    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; set; }

    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }
}

public class CgmStatus
{
    /// <summary>
    /// CGM manufacturer (e.g., "Dexcom", "Abbott", "Medtronic"). Used to register the
    /// sensor in the device registry.
    /// </summary>
    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    /// <summary>
    /// CGM model identifier (e.g., "G7", "Libre 3", "Guardian 4").
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// CGM/transmitter serial number, used to uniquely identify the device in the registry.
    /// </summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    /// <summary>
    /// Sensor age in human-readable format (e.g., "1d 4h 23m")
    /// </summary>
    [JsonPropertyName("sensorAge")]
    public string? SensorAge { get; set; }

    /// <summary>
    /// Transmitter age in human-readable format
    /// </summary>
    [JsonPropertyName("transmitterAge")]
    public string? TransmitterAge { get; set; }

    /// <summary>
    /// Signal strength percentage (0-100)
    /// </summary>
    [JsonPropertyName("signalStrength")]
    public double? SignalStrength { get; set; }

    [JsonPropertyName("calibrationStatus")]
    public string? CalibrationStatus { get; set; }

    [JsonPropertyName("lastReading")]
    public DateTime? LastReading { get; set; }

    [JsonPropertyName("sensorState")]
    public string? SensorState { get; set; }

    /// <summary>
    /// Sensor session time remaining in minutes
    /// </summary>
    [JsonPropertyName("sessionTimeRemaining")]
    public int? SessionTimeRemaining { get; set; }

    [JsonPropertyName("transmitterBattery")]
    public int? TransmitterBattery { get; set; }
}

public class MeterStatus
{
    /// <summary>
    /// Battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public int? BatteryLevel { get; set; }

    [JsonPropertyName("testStripsRemaining")]
    public int? TestStripsRemaining { get; set; }

    [JsonPropertyName("lastReading")]
    public DateTime? LastReading { get; set; }

    [JsonPropertyName("clockStatus")]
    public string? ClockStatus { get; set; }

    [JsonPropertyName("meterModel")]
    public string? MeterModel { get; set; }

    /// <summary>
    /// Memory usage percentage
    /// </summary>
    [JsonPropertyName("memoryUsage")]
    public double? MemoryUsage { get; set; }
}

public class InsulinPenStatus
{
    /// <summary>
    /// Battery level percentage (0-100)
    /// </summary>
    [JsonPropertyName("batteryLevel")]
    public int? BatteryLevel { get; set; }

    /// <summary>
    /// Remaining insulin units in the cartridge
    /// </summary>
    [JsonPropertyName("cartridgeRemaining")]
    public double? CartridgeRemaining { get; set; }

    [JsonPropertyName("lastInjection")]
    public DateTime? LastInjection { get; set; }

    [JsonPropertyName("penModel")]
    public string? PenModel { get; set; }

    [JsonPropertyName("insulinType")]
    public string? InsulinType { get; set; }

    [JsonPropertyName("needleAttached")]
    public bool? NeedleAttached { get; set; }

    [JsonPropertyName("cartridgeExpiration")]
    public DateTime? CartridgeExpiration { get; set; }
}
