using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class IndicationTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.Indication;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.Indication);
        if (MyLifeMapperHelpers.IsBatteryRemovedIndication(info))
            treatment.EventType = MyLifeTreatmentTypes.PumpBatteryChange;

        treatment.Notes = ev.InformationFromDevice;
        return new List<Treatment>
        {
            treatment
        };
    }
}