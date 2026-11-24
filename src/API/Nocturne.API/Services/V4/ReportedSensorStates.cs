using System.Globalization;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// The sensor state a device status states outright (docs/alerts/engine-semantics.md §4, sensor
/// state). Two sources say it: CareLink's <c>cgm.sensorState</c> for Medtronic Guardian sensors, and
/// the <c>xdripjs.state</c> session code that Lookout, Logger and xdrip-js send for Dexcom G5 and G6.
/// Both are read through an allowlist, and every other value is no state. An entry is added only
/// from a real upload.
/// </summary>
/// <remarks>
/// A glucose rule whose sensor is not ok holds rather than fires, so a state read wrongly silences a
/// low alarm. That is why nothing is inferred.
/// </remarks>
internal static class ReportedSensorStates
{
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

    /// <summary>
    /// The report of the state <paramref name="ds"/> states, written under its
    /// <paramref name="extras"/>' correlation id, time and source; null when it states none. CareLink's
    /// value is read first.
    /// </summary>
    public static V4Models.SensorStateReport? Of(DeviceStatus ds, V4Models.DeviceStatusExtras extras, string? source)
    {
        if (ds.Cgm?.SensorState is { } careLink && CareLink.TryGetValue(careLink, out var reported))
            return Report(reported, V4Models.SensorStateReport.CareLinkField, careLink);
        if (ds.XDripJs?.State is { } code && XDripJs.TryGetValue(code, out var session))
            return Report(session, V4Models.SensorStateReport.XDripJsField, code.ToString(CultureInfo.InvariantCulture));
        return null;

        V4Models.SensorStateReport Report(SensorState state, string field, string value) => new()
        {
            Timestamp = extras.Timestamp,
            UtcOffset = ds.UtcOffset,
            Device = string.IsNullOrEmpty(ds.Device) ? null : ds.Device,
            DataSource = source,
            CorrelationId = extras.CorrelationId,
            State = state,
            ReportedField = field,
            ReportedValue = value,
        };
    }
}
