using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Handlers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers;

internal sealed class MyLifeTreatmentMapper
{
    private static readonly IReadOnlyList<IMyLifeTreatmentHandler> Handlers =
    [
        new ManualBgTreatmentHandler(),
        new TotalDailyDoseTreatmentHandler(),
        new TempBasalTreatmentHandler(),
        new BolusTreatmentHandler(),
        new AlertTreatmentHandler(),
        new CarbCorrectionTreatmentHandler(),
        new ProfileSwitchTreatmentHandler(),
        new IndicationTreatmentHandler(),
        new PrimingTreatmentHandler(),
        new SimpleMappedTreatmentHandler(
            new Dictionary<int, string>
            {
                { MyLifeEventTypeIds.PodActivated, MyLifeTreatmentTypes.PodActivated },
                { MyLifeEventTypeIds.PodDeactivated, MyLifeTreatmentTypes.PodDeactivated },
                { MyLifeEventTypeIds.PumpSuspend, MyLifeTreatmentTypes.PumpSuspend },
                { MyLifeEventTypeIds.PumpResume, MyLifeTreatmentTypes.PumpResume },
                { MyLifeEventTypeIds.DateChanged, MyLifeTreatmentTypes.DateChanged },
                { MyLifeEventTypeIds.TimeChanged, MyLifeTreatmentTypes.TimeChanged },
                { MyLifeEventTypeIds.SiteChange, MyLifeTreatmentTypes.SiteChange },
                { MyLifeEventTypeIds.Rewind, MyLifeTreatmentTypes.Rewind },
                { MyLifeEventTypeIds.BolusMaxChanged, MyLifeTreatmentTypes.BolusMaxChanged },
                { MyLifeEventTypeIds.BasalMaxChanged, MyLifeTreatmentTypes.BasalMaxChanged }
            }
        )
    ];

    internal static IEnumerable<Treatment> MapTreatments(
        IEnumerable<MyLifeEvent> events,
        bool enableManualBgSync,
        bool enableMealCarbConsolidation,
        bool enableTempBasalConsolidation,
        int tempBasalConsolidationWindowMinutes
    )
    {
        var context = MyLifeTreatmentContext.Create(
            events,
            enableManualBgSync,
            enableMealCarbConsolidation,
            enableTempBasalConsolidation,
            tempBasalConsolidationWindowMinutes
        );
        var list = new List<Treatment>();
        foreach (var ev in events)
        {
            if (ev.Deleted) continue;

            foreach (var handler in Handlers)
            {
                if (!handler.CanHandle(ev)) continue;

                list.AddRange(handler.Handle(ev, context));

                break;
            }
        }

        return list;
    }
}