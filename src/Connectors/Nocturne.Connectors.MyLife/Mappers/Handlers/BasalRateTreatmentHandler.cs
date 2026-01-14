using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class BasalRateTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.BasalRate;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (!MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.BasalRate, out var rate))
        {
            return [];
        }

        var isTemp = MyLifeMapperHelpers.TryGetInfoBool(info, MyLifeJsonKeys.IsTempBasalRate);
        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.Basal);
        if (isTemp)
        {
            treatment.EventType = MyLifeTreatmentTypes.TempBasal;
        }
        treatment.Rate = rate;

        if (!isTemp)
        {
            return [treatment];
        }

        if (context.ShouldSuppressTempBasalRate(treatment.Mills))
        {
            return [];
        }

        if (!context.TryRegisterTempBasal(treatment.Mills))
        {
            return [];
        }

        return [treatment];
    }
}
