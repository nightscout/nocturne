using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class BasalAmountTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.Basal;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var insulin))
        {
            return [];
        }

        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.Basal);
        treatment.Insulin = insulin;
        return
        [
            treatment
        ];
    }
}