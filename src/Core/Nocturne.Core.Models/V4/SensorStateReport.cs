using NJsonSchema.Annotations;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// The state a device status reported its CGM sensor in, mapped to <see cref="SensorState"/> when the
/// status was decomposed. Alert rules read it (docs/alerts/engine-semantics.md §4, sensor state).
/// </summary>
/// <remarks>
/// A status writes one whenever it states a value in either field, and a value its source's allowlist
/// does not map writes one with no <see cref="State"/> (why is on the API's <c>ReportedSensorStates</c>).
/// It shares the status's <see cref="IV4Record.CorrelationId"/> with the
/// status's other snapshots and its <see cref="DeviceStatusExtras"/>, which still hold the section the
/// value came from. Neither source says when the state began, so there is no start.
/// </remarks>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="UploaderSnapshot"/>
[JsonSchemaFlatten]
public class SensorStateReport : V4RecordBase
{
    /// <summary>
    /// The mapped state, or null for a value outside the allowlist or a stored state this build cannot
    /// read, which reads as no state, never as ok.
    /// </summary>
    public SensorState? State { get; set; }

    /// <summary>The field the source stated it in: <see cref="CareLinkField"/> or <see cref="XDripJsField"/>.</summary>
    public string ReportedField { get; set; } = string.Empty;

    /// <summary>The value as the source sent it: a CareLink name, or an xdrip-js session code in decimal.</summary>
    public string ReportedValue { get; set; } = string.Empty;

    /// <summary>CareLink's <c>cgm.sensorState</c>, for Medtronic Guardian sensors.</summary>
    public const string CareLinkField = "cgm.sensorState";

    /// <summary>The <c>xdripjs.state</c> session code Lookout, Logger and xdrip-js send for Dexcom G5 and G6.</summary>
    public const string XDripJsField = "xdripjs.state";
}
