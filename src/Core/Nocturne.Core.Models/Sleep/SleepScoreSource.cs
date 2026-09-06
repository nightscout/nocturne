using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>Indicates whether a sleep score was provided by the wearable device or computed by Nocturne.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SleepScoreSource>))]
public enum SleepScoreSource
{
    /// <summary>Score was provided by the source device (e.g. Oura, Garmin).</summary>
    Device,

    /// <summary>Score was computed by Nocturne because the device did not provide one.</summary>
    Computed,
}
