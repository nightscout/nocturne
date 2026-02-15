using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class TotalDailyDoseTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.TotalDailyDose;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        if (!MyLifeMapperHelpers.TryGetInfoDouble(
                MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice),
                MyLifeJsonKeys.Total,
                out var total))
            return [];

        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.TotalDailyDose);
        treatment.Insulin = total;
        return
        [
            treatment
        ];
    }
}