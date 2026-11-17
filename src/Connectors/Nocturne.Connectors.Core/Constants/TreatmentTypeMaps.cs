using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Constants;

/// <summary>
/// Maps from Nightscout event type strings to the typed enums, for treatment categorisation.
/// </summary>
public static class TreatmentTypeMaps
{
    /// <summary>
    /// Map from Nightscout eventType strings to typed BolusType enum.
    /// Used for treatment categorization in chart data.
    /// </summary>
    public static readonly Dictionary<string, BolusType> BolusEventTypeMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [TreatmentTypes.Bolus] = BolusType.Bolus,
        [TreatmentTypes.MealBolus] = BolusType.MealBolus,
        [TreatmentTypes.CorrectionBolus] = BolusType.CorrectionBolus,
        [TreatmentTypes.SnackBolus] = BolusType.SnackBolus,
        [TreatmentTypes.BolusWizard] = BolusType.BolusWizard,
        [TreatmentTypes.ComboBolus] = BolusType.ComboBolus,
        [TreatmentTypes.Smb] = BolusType.Smb,
        [TreatmentTypes.AutomaticBolus] = BolusType.AutomaticBolus,
    };

    /// <summary>
    /// Map from Nightscout eventType strings to typed DeviceEventType enum.
    /// Used for treatment categorization in chart data.
    /// </summary>
    public static readonly Dictionary<string, DeviceEventType> DeviceEventTypeMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [TreatmentTypes.SensorStart] = DeviceEventType.SensorStart,
        [TreatmentTypes.SensorChange] = DeviceEventType.SensorChange,
        [TreatmentTypes.SensorStop] = DeviceEventType.SensorStop,
        [TreatmentTypes.SiteChange] = DeviceEventType.SiteChange,
        [TreatmentTypes.InsulinChange] = DeviceEventType.InsulinChange,
        [TreatmentTypes.PumpBatteryChange] = DeviceEventType.PumpBatteryChange,
        [TreatmentTypes.PodChange] = DeviceEventType.PodChange,
        [TreatmentTypes.PodActivated] = DeviceEventType.PodActivated,
        [TreatmentTypes.PodDeactivated] = DeviceEventType.PodDeactivated,
        [TreatmentTypes.ReservoirChangeEvent] = DeviceEventType.ReservoirChange,
        [TreatmentTypes.CannulaChange] = DeviceEventType.CannulaChange,
        [TreatmentTypes.TransmitterSensorInsert] = DeviceEventType.TransmitterSensorInsert,
        [TreatmentTypes.PumpSuspend] = DeviceEventType.PumpSuspend,
        [TreatmentTypes.PumpResume] = DeviceEventType.PumpResume,
    };
}
