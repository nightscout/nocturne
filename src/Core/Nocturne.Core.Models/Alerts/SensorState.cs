using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// What the sensor's own reports say of its readings (docs/alerts/engine-semantics.md §4). A state
/// other than <see cref="Ok"/> leaves the glucose leaves unknown, whatever value the reading has.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorState>))]
public enum SensorState
{
    [JsonStringEnumMemberName("warming_up")] WarmingUp,
    [JsonStringEnumMemberName("ok")] Ok,
    [JsonStringEnumMemberName("calibration_needed")] CalibrationNeeded,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("expired")] Expired,

    /// <summary>The newest reading arrived with no glucose value. The host sets it, not a source.</summary>
    [JsonStringEnumMemberName("no_value")] NoValue,
}

/// <param name="State">The state the source reported.</param>
/// <param name="Since">When it began, where the source says; null when it does not.</param>
/// <param name="Until">
/// When the host stops counting the report; null for <see cref="SensorState.NoValue"/>, which lasts as
/// long as its reading. The engine reads a state whose <paramref name="Until"/> has passed as no state.
/// </param>
public sealed record SensorStateSnapshot(SensorState State, DateTime? Since, DateTime? Until = null);
