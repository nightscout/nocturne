using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Configuration for an event type including which fields are applicable
/// </summary>
public class EventTypeConfiguration
{
    /// <summary>
    /// The event type enum value
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TreatmentEventType EventType { get; set; }

    /// <summary>
    /// Display name for the event type
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether blood glucose field is applicable
    /// </summary>
    public bool Bg { get; set; }

    /// <summary>
    /// Whether insulin field is applicable
    /// </summary>
    public bool Insulin { get; set; }

    /// <summary>
    /// Whether carbs field is applicable
    /// </summary>
    public bool Carbs { get; set; }

    /// <summary>
    /// Whether protein field is applicable
    /// </summary>
    public bool Protein { get; set; }

    /// <summary>
    /// Whether fat field is applicable
    /// </summary>
    public bool Fat { get; set; }

    /// <summary>
    /// Whether prebolus field is applicable
    /// </summary>
    public bool Prebolus { get; set; }

    /// <summary>
    /// Whether duration field is applicable
    /// </summary>
    public bool Duration { get; set; }

    /// <summary>
    /// Whether percent field is applicable
    /// </summary>
    public bool Percent { get; set; }

    /// <summary>
    /// Whether absolute field is applicable
    /// </summary>
    public bool Absolute { get; set; }

    /// <summary>
    /// Whether profile field is applicable
    /// </summary>
    public bool Profile { get; set; }

    /// <summary>
    /// Whether split field is applicable
    /// </summary>
    public bool Split { get; set; }

    /// <summary>
    /// Whether sensor field is applicable
    /// </summary>
    public bool Sensor { get; set; }
}

/// <summary>
/// Static helper for event type configurations
/// </summary>
public static class EventTypeConfigurations
{
    /// <summary>
    /// Get all event type configurations with their field applicability
    /// </summary>
    public static EventTypeConfiguration[] GetAll() =>
    [
        new() { EventType = TreatmentEventType.None, Name = "<none>", Bg = true, Insulin = true, Carbs = true, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.BgCheck, Name = "BG Check", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.SnackBolus, Name = "Snack Bolus", Bg = true, Insulin = true, Carbs = true, Protein = true, Fat = true, Prebolus = true, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.MealBolus, Name = "Meal Bolus", Bg = true, Insulin = true, Carbs = true, Protein = true, Fat = true, Prebolus = true, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.CorrectionBolus, Name = "Correction Bolus", Bg = true, Insulin = true, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.CarbCorrection, Name = "Carb Correction", Bg = true, Insulin = false, Carbs = true, Protein = true, Fat = true, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.ComboBolus, Name = "Combo Bolus", Bg = true, Insulin = true, Carbs = true, Protein = true, Fat = true, Prebolus = true, Duration = true, Percent = false, Absolute = false, Profile = false, Split = true, Sensor = false },
        new() { EventType = TreatmentEventType.Announcement, Name = "Announcement", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.Note, Name = "Note", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.Question, Name = "Question", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.SiteChange, Name = "Pump Site Change", Bg = true, Insulin = true, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.SensorStart, Name = "CGM Sensor Start", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = true },
        new() { EventType = TreatmentEventType.SensorChange, Name = "CGM Sensor Insert", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = true },
        new() { EventType = TreatmentEventType.SensorStop, Name = "CGM Sensor Stop", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.PumpBatteryChange, Name = "Pump Battery Change", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.InsulinChange, Name = "Insulin Cartridge Change", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.TempBasalStart, Name = "Temp Basal Start", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = true, Absolute = true, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.TempBasalEnd, Name = "Temp Basal End", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.ProfileSwitch, Name = "Profile Switch", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = false, Absolute = false, Profile = true, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.DadAlert, Name = "D.A.D. Alert", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.TempBasal, Name = "Temp Basal", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = true, Absolute = true, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.Exercise, Name = "Exercise", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.OpenApsOffline, Name = "OpenAPS Offline", Bg = false, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = true, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.SuspendPump, Name = "Suspend Pump", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.ResumePump, Name = "Resume Pump", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.BolusWizard, Name = "Bolus Wizard", Bg = true, Insulin = true, Carbs = true, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
        new() { EventType = TreatmentEventType.Calibration, Name = "Calibration", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = true },
        new() { EventType = TreatmentEventType.TransmitterSensorInsert, Name = "Transmitter Sensor Insert", Bg = true, Insulin = false, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = true },
        new() { EventType = TreatmentEventType.PodChange, Name = "Pod Change", Bg = true, Insulin = true, Carbs = false, Protein = false, Fat = false, Prebolus = false, Duration = false, Percent = false, Absolute = false, Profile = false, Split = false, Sensor = false },
    ];

    /// <summary>
    /// Get configuration for a specific event type
    /// </summary>
    public static EventTypeConfiguration? GetByType(TreatmentEventType eventType) =>
        GetAll().FirstOrDefault(c => c.EventType == eventType);
}
