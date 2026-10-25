using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// The uploader's verdict, from the sensor's own state, on whether a reading can be trusted.
/// </summary>
/// <remarks>
/// <c>null</c> means the uploader did not report one. The vendor's own state is kept beside it in
/// <see cref="SensorGlucose.SensorStatus"/>.
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
