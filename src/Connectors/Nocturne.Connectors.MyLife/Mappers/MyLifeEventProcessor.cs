using Nocturne.Connectors.MyLife.Mappers.Handlers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers;

/// <summary>
/// Processes MyLife events and maps them to granular models.
/// </summary>
public class MyLifeEventProcessor
{
    private static readonly IReadOnlyList<IMyLifeHandler> Handlers =
    [
        new BGCheckHandler(),
        new ProfileSwitchHandler(), // Must come before IndicationHandler
        new IndicationHandler(),
        new BolusHandler(),
        new CarbIntakeHandler(),
        new NoteHandler(),
        new PrimingHandler(),
        new DeviceEventHandler(),
    ];

    /// <summary>
    /// Maps MyLife events to SensorGlucose records.
    /// </summary>
    public IEnumerable<SensorGlucose> MapSensorGlucose(
        IEnumerable<MyLifeEvent> events,
        bool enableGlucoseSync)
    {
        return MyLifeSensorGlucoseMapper.Map(events, enableGlucoseSync);
    }

    /// <summary>
    /// Maps MyLife events to all record types (Bolus, CarbIntake, BGCheck, Note, DeviceEvent, etc.)
    /// </summary>
    public MyLifeResult MapRecords(
        IEnumerable<MyLifeEvent> events,
        bool enableManualBgSync,
        bool enableMealCarbConsolidation,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes)
    {
        var eventList = events.ToList();
        var context = MyLifeContext.Create(
            eventList,
            enableManualBgSync,
            enableMealCarbConsolidation,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes
        );

        var result = new MyLifeResult();

        foreach (var ev in eventList)
        {
            if (ev.Deleted) continue;

            foreach (var handler in Handlers)
            {
                if (!handler.CanHandle(ev)) continue;

                var records = handler.Handle(ev, context);
                foreach (var record in records)
                {
                    result.Add(record);
                }

                break;
            }
        }

        return result;
    }
}

/// <summary>
/// Result container for mapping operations, organizing records by type.
/// </summary>
public class MyLifeResult
{
    public List<Bolus> Boluses { get; } = [];
    public List<CarbIntake> CarbIntakes { get; } = [];
    public List<BGCheck> BGChecks { get; } = [];
    public List<BolusCalculation> BolusCalculations { get; } = [];
    public List<Note> Notes { get; } = [];
    public List<DeviceEvent> DeviceEvents { get; } = [];

    internal void Add(IV4Record record)
    {
        switch (record)
        {
            case Bolus bolus:
                Boluses.Add(bolus);
                break;
            case CarbIntake carbIntake:
                CarbIntakes.Add(carbIntake);
                break;
            case BGCheck bgCheck:
                BGChecks.Add(bgCheck);
                break;
            case BolusCalculation bolusCalculation:
                BolusCalculations.Add(bolusCalculation);
                break;
            case Note note:
                Notes.Add(note);
                break;
            case DeviceEvent deviceEvent:
                DeviceEvents.Add(deviceEvent);
                break;
        }
    }

    /// <summary>
    /// Gets all records as a flat list.
    /// </summary>
    public IEnumerable<IV4Record> AllRecords =>
        Boluses.Cast<IV4Record>()
            .Concat(CarbIntakes)
            .Concat(BGChecks)
            .Concat(BolusCalculations)
            .Concat(Notes)
            .Concat(DeviceEvents);

    /// <summary>
    /// Gets the total count of all records.
    /// </summary>
    public int TotalCount =>
        Boluses.Count +
        CarbIntakes.Count +
        BGChecks.Count +
        BolusCalculations.Count +
        Notes.Count +
        DeviceEvents.Count;
}
