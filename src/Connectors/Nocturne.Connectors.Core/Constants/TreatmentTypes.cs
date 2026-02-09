using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Constants;

/// <summary>
/// Standard treatment type strings for consistency across connectors.
/// These match Nightscout's expected event types.
/// </summary>
public static class TreatmentTypes
{
    /// <summary>
    /// Treatment with both carbs and insulin (meal with bolus).
    /// </summary>
    public const string MealBolus = "Meal Bolus";

    /// <summary>
    /// Treatment with insulin only (correction for high blood glucose).
    /// </summary>
    public const string CorrectionBolus = "Correction Bolus";

    /// <summary>
    /// Treatment with carbs only (eating without bolusing).
    /// </summary>
    public const string CarbCorrection = "Carb Correction";

    /// <summary>
    /// Temporary basal rate change.
    /// </summary>
    public const string TempBasal = "Temp Basal";

    /// <summary>
    /// Regular basal insulin delivery.
    /// </summary>
    public const string Basal = "Basal";

    /// <summary>
    /// Blood glucose check (finger stick or CGM calibration).
    /// </summary>
    public const string BgCheck = "BG Check";

    /// <summary>
    /// Automatic bolus from AID system.
    /// </summary>
    public const string AutomaticBolus = "Automatic Bolus";

    /// <summary>
    /// Pump alarm event.
    /// </summary>
    public const string PumpAlarm = "Pump Alarm";

    /// <summary>
    /// Reservoir/cartridge change.
    /// </summary>
    public const string ReservoirChange = "Reservoir Change";

    /// <summary>
    /// Infusion site change.
    /// </summary>
    public const string SiteChange = "Site Change";

    /// <summary>
    /// Profile switch event.
    /// </summary>
    public const string ProfileSwitch = "Profile Switch";

    /// <summary>
    /// Super Micro Bolus (automated by AID systems).
    /// </summary>
    public const string Smb = "SMB";

    /// <summary>
    /// Snack bolus.
    /// </summary>
    public const string SnackBolus = "Snack Bolus";

    /// <summary>
    /// Bolus wizard calculated bolus.
    /// </summary>
    public const string BolusWizard = "Bolus Wizard";

    /// <summary>
    /// Combo/dual wave bolus.
    /// </summary>
    public const string ComboBolus = "Combo Bolus";

    /// <summary>
    /// Generic bolus event type.
    /// </summary>
    public const string Bolus = "Bolus";

    /// <summary>
    /// Sensor start event.
    /// </summary>
    public const string SensorStart = "Sensor Start";

    /// <summary>
    /// Sensor change event.
    /// </summary>
    public const string SensorChange = "Sensor Change";

    /// <summary>
    /// Sensor stop event.
    /// </summary>
    public const string SensorStop = "Sensor Stop";

    /// <summary>
    /// Insulin/reservoir change event (alternate name for ReservoirChange).
    /// </summary>
    public const string InsulinChange = "Insulin Change";

    /// <summary>
    /// Pump battery change event.
    /// </summary>
    public const string PumpBatteryChange = "Pump Battery Change";

    /// <summary>
    /// Map from Nightscout eventType strings to typed BolusType enum.
    /// Used for treatment categorization in chart data.
    /// </summary>
    public static readonly Dictionary<string, BolusType> BolusEventTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [Bolus] = BolusType.Bolus,
        [MealBolus] = BolusType.MealBolus,
        [CorrectionBolus] = BolusType.CorrectionBolus,
        [SnackBolus] = BolusType.SnackBolus,
        [BolusWizard] = BolusType.BolusWizard,
        [ComboBolus] = BolusType.ComboBolus,
        [Smb] = BolusType.Smb,
        [AutomaticBolus] = BolusType.AutomaticBolus,
    };

    /// <summary>
    /// Map from Nightscout eventType strings to typed DeviceEventType enum.
    /// Used for treatment categorization in chart data.
    /// </summary>
    public static readonly Dictionary<string, DeviceEventType> DeviceEventTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [SensorStart] = DeviceEventType.SensorStart,
        [SensorChange] = DeviceEventType.SensorChange,
        [SensorStop] = DeviceEventType.SensorStop,
        [SiteChange] = DeviceEventType.SiteChange,
        [InsulinChange] = DeviceEventType.InsulinChange,
        [PumpBatteryChange] = DeviceEventType.PumpBatteryChange,
    };
}
