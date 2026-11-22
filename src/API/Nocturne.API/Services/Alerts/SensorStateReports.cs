using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The sensor state a device status states outright (docs/alerts/engine-semantics.md §4, sensor
/// state). Two sources say it: CareLink's <c>cgm.sensorState</c> for Medtronic Guardian sensors, and
/// the <c>xdripjs.state</c> session code that Lookout, Logger and xdrip-js send for Dexcom G5 and G6.
/// Both are read through an allowlist, and every other value is no state. An entry is added only
/// from a real upload.
/// </summary>
/// <remarks>
/// A glucose rule whose sensor is not ok holds rather than fires, so a state read wrongly silences a
/// low alarm. That is why nothing is inferred, why a state counts only for the reading it describes
/// (<see cref="Describes"/>), and why it counts only while its report is recent: a source that stops
/// reporting leaves no state behind. Neither source says when its state began.
/// </remarks>
internal static class SensorStateReports
{
    /// <summary>
    /// How long a report counts after it arrived: the engine's default glucose freshness bound, so a
    /// state does not outlive the reading it came with.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(11);

    private static readonly IReadOnlyDictionary<string, SensorState> CareLink =
        new Dictionary<string, SensorState>(StringComparer.Ordinal)
        {
            ["NO_ERROR_MESSAGE"] = SensorState.Ok,
            ["WARM_UP"] = SensorState.WarmingUp,
            ["CALIBRATION_REQUIRED"] = SensorState.CalibrationNeeded,
            ["SENSOR_ERROR"] = SensorState.Error,
            ["CHANGE_SENSOR"] = SensorState.Error,
            ["SENSOR_END_OF_LIFE"] = SensorState.Expired,
        };

    /// <summary>
    /// Dexcom G5/G6 transmitter session codes, as xdrip-js names them. The routine calibration prompts
    /// (<c>0x07</c>, <c>0x0e</c>) are absent: the sensor still reports glucose through them.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, SensorState> XDripJs = new Dictionary<int, SensorState>
    {
        [0x02] = SensorState.WarmingUp,
        [0x04] = SensorState.CalibrationNeeded,
        [0x05] = SensorState.CalibrationNeeded,
        [0x06] = SensorState.Ok,
        [0x08] = SensorState.CalibrationNeeded,
        [0x09] = SensorState.CalibrationNeeded,
        [0x0a] = SensorState.CalibrationNeeded,
        [0x0b] = SensorState.Error,
        [0x0c] = SensorState.Error,
        [0x0d] = SensorState.CalibrationNeeded,
        [0x0f] = SensorState.Expired,
        [0x10] = SensorState.Error,
        [0x11] = SensorState.Error,
        [0x12] = SensorState.Error,
    };

    /// <summary>When a report arrived, as far as it can be told: the earlier of its timestamp and its row's.</summary>
    public static DateTime ArrivedAt(DeviceStatusExtras status) =>
        status.Timestamp < status.CreatedAt ? status.Timestamp : status.CreatedAt;

    /// <summary>
    /// The state the newest report of <paramref name="source"/>'s reading states at
    /// <paramref name="asOf"/>, with the instant it stops counting; null when none does. A report
    /// counts while its timestamp is within the clock tolerance of <paramref name="asOf"/>
    /// (<see cref="AlertReadings.Bound"/>) and it arrived at most <see cref="MaxAge"/> before; reports
    /// are ordered by arrival, so one dated ahead does not outrank a newer one.
    /// </summary>
    public static (SensorState State, DateTime Until)? Newest(
        IEnumerable<DeviceStatusExtras> statuses, DateTime asOf, ReadingSource? source) =>
        statuses
            .Where(s => s.Timestamp <= AlertReadings.Bound(asOf)
                        && s.CreatedAt <= asOf
                        && asOf - ArrivedAt(s) <= MaxAge
                        && Describes(s, source))
            .OrderByDescending(ArrivedAt)
            .Select(s => Of(s) is { } state ? (state, ArrivedAt(s) + MaxAge) : ((SensorState, DateTime)?)null)
            .FirstOrDefault(found => found is not null);

    /// <summary>
    /// Whether <paramref name="status"/> is a report of the device <paramref name="source"/>'s reading
    /// came from: its device and its data source are both present on both, and equal. CareLink names a
    /// status and its readings <c>CareLink {family}</c> and writes both under its connector's source. A
    /// status or a reading that lacks either says nothing about the other, so it is not one.
    /// </summary>
    public static bool Describes(DeviceStatusExtras status, ReadingSource? source) =>
        source is { Device: { Length: > 0 } device, DataSource: { Length: > 0 } dataSource }
        && string.Equals(status.Device, device, StringComparison.Ordinal)
        && string.Equals(status.DataSource, dataSource, StringComparison.Ordinal);

    /// <summary>The state <paramref name="status"/> states, or null when it states none.</summary>
    public static SensorState? Of(DeviceStatusExtras status)
    {
        if (status.Extras is not { } extras)
            return null;
        if (Member(extras, DeviceStatusExtras.CgmSection, DeviceStatusExtras.CgmSensorState)
                is { ValueKind: JsonValueKind.String } careLink
            && CareLink.TryGetValue(careLink.GetString()!, out var reported))
            return reported;
        if (Member(extras, DeviceStatusExtras.XDripJsSection, DeviceStatusExtras.XDripJsState)
                is { ValueKind: JsonValueKind.Number } code
            && code.TryGetInt32(out var value)
            && XDripJs.TryGetValue(value, out var session))
            return session;
        return null;
    }

    private static JsonElement? Member(IReadOnlyDictionary<string, object?> extras, string section, string name) =>
        extras.TryGetValue(section, out var value)
        && value is JsonElement { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var member)
            ? member
            : null;
}
