using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Sleep stage classification within a sleep session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepStageType>))]
public enum SleepStageType
{
    /// <summary>
    /// Stage is not known (e.g. hypo nadir falls outside any recorded stage interval)
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// User is in bed (aggregate state, no specific stage)
    /// </summary>
    InBed,

    /// <summary>
    /// Awake period during the night
    /// </summary>
    Awake,

    /// <summary>
    /// Awake but still in bed
    /// </summary>
    AwakeInBed,

    /// <summary>
    /// Out of bed (e.g. bathroom, getting water)
    /// </summary>
    OutOfBed,

    /// <summary>
    /// Light (N1/N2) sleep stage
    /// </summary>
    Light,

    /// <summary>
    /// Deep (N3/slow-wave) sleep stage
    /// </summary>
    Deep,

    /// <summary>
    /// Rapid eye movement sleep stage
    /// </summary>
    Rem,

    /// <summary>
    /// Asleep but stage not differentiated
    /// </summary>
    Asleep,

    /// <summary>
    /// Restless movement detected during sleep
    /// </summary>
    Restless,

    /// <summary>
    /// Stage could not be determined by the device
    /// </summary>
    Unmeasurable
}

/// <summary>
/// Classification of a sleep session by duration and intent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepSessionType>))]
public enum SleepSessionType
{
    /// <summary>
    /// Primary overnight sleep session
    /// </summary>
    Overnight,

    /// <summary>
    /// Short daytime sleep
    /// </summary>
    Nap,

    /// <summary>
    /// Rest period without significant sleep
    /// </summary>
    Rest,

    /// <summary>
    /// Session type not determined
    /// </summary>
    Unknown
}

/// <summary>
/// How the sleep session boundaries were detected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepDetectionMethod>))]
public enum SleepDetectionMethod
{
    /// <summary>
    /// Automatically detected by device algorithms
    /// </summary>
    Auto,

    /// <summary>
    /// Manually recorded by the user
    /// </summary>
    Manual,

    /// <summary>
    /// Preliminary automatic detection (may be revised)
    /// </summary>
    AutoTentative,

    /// <summary>
    /// Final automatic detection after server-side refinement
    /// </summary>
    AutoFinal,

    /// <summary>
    /// Enhanced detection using additional sensors
    /// </summary>
    Enhanced,

    /// <summary>
    /// Final enhanced detection after server-side refinement
    /// </summary>
    EnhancedFinal,

    /// <summary>
    /// Detected by a dedicated sleep-tracking device
    /// </summary>
    Device,

    /// <summary>
    /// Detection method not known
    /// </summary>
    Unknown
}

/// <summary>
/// Origin platform or device that recorded the sleep data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepSource>))]
public enum SleepSource
{
    /// <summary>
    /// Apple Health / Apple Watch
    /// </summary>
    Apple,

    /// <summary>
    /// Google Health Connect / Fitbit (Google ecosystem)
    /// </summary>
    Google,

    /// <summary>
    /// Fitbit (legacy, pre-Google integration)
    /// </summary>
    Fitbit,

    /// <summary>
    /// Oura Ring
    /// </summary>
    Oura,

    /// <summary>
    /// Garmin Connect
    /// </summary>
    Garmin,

    /// <summary>
    /// Samsung Health
    /// </summary>
    Samsung,

    /// <summary>
    /// Manually entered by the user
    /// </summary>
    Manual
}
