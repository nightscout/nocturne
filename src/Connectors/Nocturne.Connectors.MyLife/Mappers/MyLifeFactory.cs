using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
/// Factory for creating model instances from MyLife events.
/// Provides common initialization logic for all record types.
/// </summary>
internal static class MyLifeFactory
{
    internal static T CreateBase<T>(MyLifeEvent ev, DateTime now)
        where T : IV4Record, new()
    {
        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);

        return new T
        {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp.UtcDateTime,
            LegacyId = $"{MyLifeIdPrefixes.Treatment}{MyLifeMapperHelpers.BuildEventKey(ev)}",
            DataSource = DataSources.MyLifeConnector,
            CreatedAt = now,
            ModifiedAt = now,
        };
    }

    internal static T CreateBaseWithSuffix<T>(MyLifeEvent ev, DateTime now, string suffix)
        where T : IV4Record, new()
    {
        var record = CreateBase<T>(ev, now);
        if (!string.IsNullOrWhiteSpace(suffix))
            record.LegacyId = $"{record.LegacyId}-{suffix}";
        return record;
    }

    internal static SensorGlucose CreateSensorGlucose(MyLifeEvent ev, DateTime now, double mgdl)
    {
        var timestamp = MyLifeMapperHelpers.FromInstantTicks(ev.EventDateTime);

        return new SensorGlucose
        {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp.UtcDateTime,
            LegacyId = $"{MyLifeIdPrefixes.Entry}{MyLifeMapperHelpers.BuildEventKey(ev)}",
            DataSource = DataSources.MyLifeConnector,
            CreatedAt = now,
            ModifiedAt = now,
            Mgdl = mgdl,
        };
    }

    internal static Bolus CreateBolus(
        MyLifeEvent ev,
        DateTime now,
        double insulin,
        Nocturne.Core.Models.V4.BolusType? bolusType = null,
        double? duration = null,
        Guid? correlationId = null
    )
    {
        var bolus = CreateBase<Bolus>(ev, now);
        bolus.Insulin = insulin;
        bolus.BolusType = bolusType;
        bolus.Duration = duration;
        bolus.CorrelationId = correlationId;
        return bolus;
    }

    internal static CarbIntake CreateCarbIntake(
        MyLifeEvent ev,
        DateTime now,
        double carbs,
        Guid? correlationId = null
    )
    {
        var carbIntake = CreateBase<CarbIntake>(ev, now);
        carbIntake.Carbs = carbs;
        carbIntake.CorrelationId = correlationId;
        return carbIntake;
    }

    internal static BGCheck CreateBGCheck(MyLifeEvent ev, DateTime now, double glucose)
    {
        var bgCheck = CreateBase<BGCheck>(ev, now);
        bgCheck.Glucose = glucose;
        bgCheck.GlucoseType = GlucoseType.Finger;
        bgCheck.Units = GlucoseUnit.MgDl;
        return bgCheck;
    }

    internal static Note CreateNote(
        MyLifeEvent ev,
        DateTime now,
        string text,
        string? eventType = null,
        bool isAnnouncement = false
    )
    {
        var note = CreateBase<Note>(ev, now);
        note.Text = text;
        note.EventType = eventType;
        note.IsAnnouncement = isAnnouncement;
        return note;
    }

    internal static DeviceEvent CreateDeviceEvent(
        MyLifeEvent ev,
        DateTime now,
        DeviceEventType eventType,
        string? notes = null
    )
    {
        var deviceEvent = CreateBase<DeviceEvent>(ev, now);
        deviceEvent.EventType = eventType;
        deviceEvent.Notes = notes;
        return deviceEvent;
    }

    internal static BolusCalculation CreateBolusCalculation(
        MyLifeEvent ev,
        DateTime now,
        double? bloodGlucoseInput = null,
        double? carbInput = null,
        double? insulinOnBoard = null,
        double? insulinRecommendation = null,
        Guid? correlationId = null
    )
    {
        var calc = CreateBase<BolusCalculation>(ev, now);
        calc.BloodGlucoseInput = bloodGlucoseInput;
        calc.CarbInput = carbInput;
        calc.InsulinOnBoard = insulinOnBoard;
        calc.InsulinRecommendation = insulinRecommendation;
        calc.CorrelationId = correlationId;
        return calc;
    }
}
