using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class TempBasalTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.TempBasal;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.TempBasal);
        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Percentage, out var percent))
        {
            treatment.Percent = percent;
        }

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Minutes, out var minutes))
        {
            treatment.Duration = minutes;
        }

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.ValueInUperH, out var rate))
        {
            treatment.Rate = rate;
        }

        if (!context.TryRegisterTempBasal(treatment.Mills))
        {
            return [];
        }

        if (context.TryGetTempBasalRate(treatment.Mills, out var consolidatedRate))
        {
            treatment.Rate = consolidatedRate;
        }

        return [treatment];
    }
}
