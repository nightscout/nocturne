using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles standalone MyLife carb correction events, creating CarbIntake records.
/// Carb events that are consolidated with boluses are handled by BolusHandler instead.
/// </summary>
internal sealed class CarbIntakeHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.CarbCorrection;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var carbs))
            return [];

        // If meal carb consolidation is enabled and this carb event was
        // matched to a bolus, suppress it (the carbs are in the bolus handler)
        if (context.EnableMealCarbConsolidation)
        {
            var mills = MyLifeMapperHelpers.ToUnixMilliseconds(ev.EventDateTime);
            if (context.SuppressedCarbTimes.Contains(mills))
                return [];
        }

        var carbIntake = MyLifeFactory.CreateCarbIntake(ev, carbs);
        return [carbIntake];
    }
}
