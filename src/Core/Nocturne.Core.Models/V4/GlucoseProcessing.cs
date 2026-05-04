using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Whether a glucose value has been algorithmically smoothed or is a raw sensor reading.
/// </summary>
/// <remarks>
/// <c>null</c> represents unknown processing status.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseProcessing>))]
public enum GlucoseProcessing
{
    /// <summary>Value has been algorithmically smoothed (e.g., XDrip default, Dexcom EGV).</summary>
    Smoothed,

    /// <summary>Raw sensor glucose value without smoothing (e.g., Juggluco default).</summary>
    Unsmoothed
}
