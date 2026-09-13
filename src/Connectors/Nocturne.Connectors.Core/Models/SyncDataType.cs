using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncDataType
{
    Glucose,
    ManualBG,
    Calibrations,
    Boluses,
    BasalInjections,
    CarbIntake,
    BGChecks,
    BolusCalculations,
    Notes,
    DeviceEvents,
    StateSpans,
    TempBasals,
    Profiles,
    DeviceStatus,
    Activity,
    Food,
    Steps,
    HeartRate,
    BodyWeight,
    Sleep
}
