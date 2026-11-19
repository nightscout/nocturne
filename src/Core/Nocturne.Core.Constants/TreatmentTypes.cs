namespace Nocturne.Core.Constants;

/// <summary>
///     Nightscout legacy treatment event types and the lists of them.
///     These match Nightscout's expected event types.
/// </summary>
public static class TreatmentTypes
{
    /// <summary>
    ///     Treatment with both carbs and insulin (meal with bolus).
    /// </summary>
    public const string MealBolus = "Meal Bolus";

    /// <summary>
    ///     Treatment with insulin only (correction for high blood glucose).
    /// </summary>
    public const string CorrectionBolus = "Correction Bolus";

    /// <summary>
    ///     Treatment with carbs only (eating without bolusing).
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
    /// Pump insulin delivery suspended.
    /// </summary>
    public const string PumpSuspend = "Pump Suspend";

    /// <summary>
    /// Pump insulin delivery resumed.
    /// </summary>
    public const string PumpResume = "Pump Resume";

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
    /// Pod change event (Omnipod).
    /// </summary>
    public const string PodChange = "Pod Change";

    /// <summary>
    /// Pod activated: a new pod is primed and delivering. MyLife and Glooko report a pod change as
    /// this pair rather than as one Pod Change.
    /// </summary>
    public const string PodActivated = "Pod Activated";

    /// <summary>
    /// Pod deactivated: the pod has stopped delivering and is to come off.
    /// </summary>
    public const string PodDeactivated = "Pod Deactivated";

    /// <summary>
    /// Reservoir/cartridge change event.
    /// </summary>
    public const string ReservoirChangeEvent = "Reservoir Change";

    /// <summary>
    /// Cannula change event.
    /// </summary>
    public const string CannulaChange = "Cannula Change";

    /// <summary>
    /// Transmitter or sensor insert event.
    /// </summary>
    public const string TransmitterSensorInsert = "Transmitter Sensor Insert";

    /// <summary>
    /// Temporary override (Loop and Trio) in force for a span of time.
    /// </summary>
    public const string TemporaryOverride = "Temporary Override";

    /// <summary>
    /// Cancels the temporary override in force.
    /// </summary>
    public const string TemporaryOverrideCancel = "Temporary Override Cancel";

    /// <summary>
    /// Temporary glucose target in force for a span of time.
    /// </summary>
    public const string TemporaryTarget = "Temporary Target";

    /// <summary>
    /// Cancels the temporary target in force.
    /// </summary>
    public const string TemporaryTargetCancel = "Temporary Target Cancel";

    /// <summary>
    /// Free-text note.
    /// </summary>
    public const string Note = "Note";

    /// <summary>
    /// Announcement: a note that Nightscout shows as an alert.
    /// </summary>
    public const string Announcement = "Announcement";

    /// <summary>
    /// Exercise, recorded as a note.
    /// </summary>
    public const string Exercise = "Exercise";

    /// <summary>
    /// Insulin delivered outside the pump, such as a pen dose.
    /// </summary>
    public const string ExternalInsulin = "External Insulin";

    /// <summary>
    /// Carbs as oref treatments and the demo data carry them. The careportal sends <see cref="CarbCorrection"/> for carbs with no bolus.
    /// </summary>
    public const string Carbs = "Carbs";

    /// <summary>
    /// OpenAPS offline: the loop was not running for the span of the event.
    /// </summary>
    public const string OpenApsOffline = "OpenAPS Offline";

    /// <summary>
    /// Temp basal start recorded under its own name.
    /// </summary>
    public const string TempBasalStart = "Temp Basal Start";

    /// <summary>
    /// Temp basal spelt as one word, as OpenAPS-family uploaders send it.
    /// </summary>
    public const string TempBasalOneWord = "TempBasal";

    /// <summary>
    /// Every event type that records a temp basal. Compare case-insensitively.
    /// </summary>
    public static readonly IReadOnlyList<string> TempBasalEventTypes =
    [
        TempBasal,
        TempBasalStart,
        TempBasalOneWord,
    ];

    /// <summary>
    /// The bolus event types <c>TreatmentService</c> stamps with the patient's primary bolus insulin
    /// on create. Compare case-insensitively.
    /// </summary>
    public static readonly IReadOnlyList<string> InsulinContextBolusEventTypes =
    [
        SnackBolus,
        MealBolus,
        CorrectionBolus,
        ComboBolus,
    ];

    /// <summary>
    /// The basal event types <c>TreatmentService</c> stamps with the patient's primary basal insulin
    /// on create. Holds <see cref="TempBasalEventTypes"/> without <see cref="TempBasalOneWord"/>.
    /// Compare case-insensitively.
    /// </summary>
    public static readonly IReadOnlyList<string> InsulinContextBasalEventTypes =
    [
        TempBasal,
        TempBasalStart,
    ];
}
