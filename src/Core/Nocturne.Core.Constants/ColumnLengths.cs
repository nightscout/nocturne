namespace Nocturne.Core.Constants;

/// <summary>
/// Lengths of the string columns a V4 write body is capped at. The entity attribute and the request
/// validator of a column read the same constant, so the cap and the column cannot differ. A column
/// of a record table's header is in <see cref="RecordColumnLengths"/>.
/// </summary>
public static class ColumnLengths
{
    /// <summary>The string columns of a bolus that a client sets.</summary>
    public static class Bolus
    {
        public const int InsulinType = 128;
    }

    /// <summary>The string columns of a bolus calculation that a client sets.</summary>
    public static class BolusCalculation
    {
        public const int BloodGlucoseInputSource = 256;
    }

    /// <summary>The string columns of a note that a client sets.</summary>
    public static class Note
    {
        public const int EventType = 256;
    }

    /// <summary>The string columns of an APS snapshot that a client sets.</summary>
    public static class ApsSnapshot
    {
        public const int AidVersion = 64;
    }

    /// <summary>The string columns of an uploader snapshot that a client sets.</summary>
    public static class UploaderSnapshot
    {
        public const int Name = 256;
        public const int Type = 128;
    }

    /// <summary>The string columns of a therapy settings record that a client sets.</summary>
    public static class TherapySettings
    {
        public const int Timezone = 64;
        public const int Units = 10;
        public const int EnteredBy = 100;
        public const int StartDate = 50;
    }

    /// <summary>The string columns of a sleep session.</summary>
    public static class SleepSession
    {
        public const int Timezone = 100;
        public const int SourceDevice = 200;
        public const int SourceApp = 200;
        public const int OriginalId = 255;
    }

    /// <summary>The string columns of a system event.</summary>
    /// <summary>The string column of a page view count.</summary>
    public static class PageViewCount
    {
        public const int RouteId = 200;
    }

    public static class SystemEvent
    {
        public const int Code = 100;
        public const int Description = 1000;
        public const int Source = 50;
        public const int OriginalId = 255;
    }

    /// <summary>The string columns of a food.</summary>
    public static class Food
    {
        public const int Type = 50;
        public const int Category = 200;
        public const int Subcategory = 200;
        public const int Name = 500;
        public const int Unit = 30;
        public const int ExternalId = 255;
    }

    /// <summary>The string columns of a patient record.</summary>
    public static class PatientRecord
    {
        public const int DiabetesTypeOther = 256;
        public const int PreferredName = 256;
        public const int Pronouns = 64;
        public const int AvatarUrl = 2048;
        public const int Timezone = 64;
    }

    /// <summary>The string columns of a patient insulin.</summary>
    public static class PatientInsulin
    {
        public const int Name = 256;
        public const int Notes = 4096;
        public const int FormulationId = 64;
        public const int Curve = 32;
    }

    /// <summary>The string columns of a patient device.</summary>
    public static class PatientDevice
    {
        public const int Manufacturer = 256;
        public const int Model = 256;
        public const int CatalogId = 64;
        public const int SerialNumber = 256;
        public const int Notes = 4096;
    }

    /// <summary>The string columns of a clock face.</summary>
    public static class ClockFace
    {
        public const int Name = 255;
    }

    /// <summary>The string columns of a flag rule.</summary>
    public static class FlagRule
    {
        public const int Dataset = 64;
        public const int Field = 64;
    }

    /// <summary>The string columns of a do-not-disturb window.</summary>
    public static class DndWindow
    {
        public const int Source = 128;
    }

    /// <summary>The string columns of a tracker definition.</summary>
    public static class TrackerDefinition
    {
        public const int Name = 255;
        public const int Description = 1000;
        public const int Icon = 100;
        public const int TriggerNotesContains = 255;
        public const int StartEventType = 100;
        public const int CompletionEventType = 100;
    }

    /// <summary>The string columns of a tracker preset.</summary>
    public static class TrackerPreset
    {
        public const int Name = 255;
        public const int DefaultStartNotes = 1000;
    }

    /// <summary>The string columns of an alert rule.</summary>
    public static class AlertRule
    {
        public const int Name = 128;
        public const int Description = 512;
    }

    /// <summary>The string columns of an in-app notification.</summary>
    public static class InAppNotification
    {
        public const int Type = 100;
        public const int Title = 255;
        public const int Subtitle = 500;
        public const int SourceId = 255;
        public const int Icon = 50;
        public const int Source = 100;
    }

    /// <summary>The string columns of the food of a treatment.</summary>
    public static class TreatmentFood
    {
        public const int Note = 1000;
    }

    /// <summary>The string columns of a state span.</summary>
    public static class StateSpan
    {
        public const int State = 100;
        public const int Source = 50;
        public const int LegacyId = 255;
    }

    /// <summary>The string columns of a basal injection.</summary>
    public static class BasalInjection
    {
        public const int Notes = 4096;
    }

    /// <summary>The string columns of a pump snapshot.</summary>
    public static class PumpSnapshot
    {
        public const int Manufacturer = 128;
        public const int Model = 128;
        public const int ReservoirDisplay = 64;
        public const int PumpStatus = 64;
        public const int PumpMode = 64;
        public const int Clock = 64;
    }

    /// <summary>The string columns of a body weight sample.</summary>
    public static class BodyWeight
    {
        public const int CreatedAt = 50;
    }

    /// <summary>The string columns of a lab HbA1c result.</summary>
    public static class LabHbA1cResult
    {
        public const int Note = 500;
    }

    /// <summary>The string columns of a tracker instance.</summary>
    public static class TrackerInstance
    {
        public const int StartTreatmentId = 255;
        public const int CompleteTreatmentId = 255;
        public const int StartNotes = 1000;
        public const int CompletionNotes = 1000;
    }

    /// <summary>The string columns of a tracker notification threshold.</summary>
    public static class TrackerNotificationThreshold
    {
        public const int Description = 500;
    }

    /// <summary>The string columns of a food entry imported by a connector.</summary>
    public static class ConnectorFoodEntry
    {
        public const int ConnectorSource = 50;
        public const int ExternalEntryId = 255;
        public const int ExternalFoodId = 255;
        public const int MealName = 50;
        public const int ServingDescription = 100;
    }

    /// <summary>The string columns of a timezone timeline entry.</summary>
    public static class TimezoneTimeline
    {
        public const int Timezone = 64;
    }

    /// <summary>The string columns of an alert excursion.</summary>
    public static class AlertExcursion
    {
        public const int AcknowledgedBy = 256;
    }

    /// <summary>The string columns of an alert delivery.</summary>
    public static class AlertDelivery
    {
        public const int PlatformMessageId = 256;
        public const int PlatformThreadId = 256;
    }

    /// <summary>The string columns of a coach mark state.</summary>
    public static class CoachMarkState
    {
        public const int Status = 50;
    }

    /// <summary>The string columns of a tenant that a setup body writes.</summary>
    public static class Tenant
    {
        public const int SetupStripDismissedRevision = 64;
    }

    /// <summary>The string columns of a discrepancy analysis.</summary>
    public static class DiscrepancyAnalysis
    {
        public const int TraceId = 128;
        public const int RequestMethod = 10;
        public const int RequestPath = 2048;
        public const int Summary = 1000;
        public const int SelectedResponseTarget = 50;
        public const int SelectionReason = 500;
        public const int ErrorMessage = 2000;
    }

    /// <summary>The string columns of a discrepancy detail.</summary>
    public static class DiscrepancyDetail
    {
        public const int Field = 500;
        public const int NightscoutValue = 2000;
        public const int NocturneValue = 2000;
        public const int Description = 1000;
    }
}
