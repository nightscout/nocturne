using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class CarbCorrectionTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.CarbCorrection;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var carbs))
        {
            return [];
        }

        // Use shared TreatmentTypes constant for consistency across connectors
        var treatment = MyLifeTreatmentFactory.Create(ev, TreatmentTypes.CarbCorrection);
        treatment.Carbs = carbs;

        if (context.EnableMealCarbConsolidation &&
            context.SuppressedCarbTimes.Contains(treatment.Mills))
        {
            return [];
        }

        return [treatment];
    }
}
