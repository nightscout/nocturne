using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncDataType
{
    Glucose,
    ManualBG,
    Calibrations,
    Boluses,
    CarbIntake,
    BGChecks,
    BolusCalculations,
    Notes,
    DeviceEvents,
    StateSpans,
    Profiles,
    DeviceStatus,
    Activity,
    Food
}
