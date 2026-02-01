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
}
