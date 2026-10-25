using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// The uploader's verdict, from the sensor's own state, on whether a reading can be trusted.
/// </summary>
/// <remarks>
/// <c>null</c> means the uploader did not report one, and the reading counts as before.
/// <see cref="Invalid"/> readings are stored and shown, marked, but statistics and alerts leave
/// them out (<see cref="SensorGlucoseQualityRules"/>); every other value counts.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SensorGlucoseQuality>))]
public enum SensorGlucoseQuality
{
    /// <summary>
    /// The sensor reported the reading as reliable, even if it held back a trend or a prediction
    /// (a Dexcom G7 reading whose secondary state is not 15).
    /// </summary>
    Valid,

    /// <summary>The sensor kept the value but flagged it as less reliable than usual.</summary>
    Uncertain,

    /// <summary>
    /// The sensor marked the value as not glucose, e.g. a Dexcom G7 <c>Errors</c> state. A reader
    /// that reports warm-up or a failed sensor with a value sends this, with the state in
    /// <see cref="SensorGlucose.SensorStatus"/>.
    /// </summary>
    Invalid,
}

/// <summary>
/// The one rule for which readings statistics and alerts use.
/// </summary>
public static class SensorGlucoseQualityRules
{
    /// <summary>
    /// False for <see cref="SensorGlucoseQuality.Invalid"/>: the sensor itself said the value is
    /// not glucose. Leaving it out turns an error episode into missing data, so time-in-range
    /// treats it as uncovered time rather than time out of range.
    /// </summary>
    public static bool CountsInStatistics(this SensorGlucose reading) =>
        reading.Quality != SensorGlucoseQuality.Invalid;

    /// <summary>The stored form of <see cref="SensorGlucoseQuality.Invalid"/>, for query filters.</summary>
    public const string InvalidStored = nameof(SensorGlucoseQuality.Invalid);
}
