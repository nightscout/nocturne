using System.Text.Json;
using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class BolusTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId is
            MyLifeEventTypeIds.BolusNormal
            or MyLifeEventTypeIds.BolusSquare
            or MyLifeEventTypeIds.BolusDual;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (!MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.AmountOfBolus, out var insulin))
        {
            return [];
        }

        var isCalculated = MyLifeMapperHelpers.IsCalculatedBolus(info);
        var carbs = MyLifeMapperHelpers.ResolveBolusCarbs(info);
        if (context.BolusCarbMatches.TryGetValue(MyLifeMapperHelpers.BuildEventKey(ev), out var matchedCarbs))
        {
            carbs = matchedCarbs;
        }

        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.CorrectionBolus);
        if (isCalculated && carbs is > 0)
        {
            treatment.EventType = MyLifeTreatmentTypes.MealBolus;
        }

        treatment.Insulin = insulin;
        treatment.BolusType = ev.EventTypeId switch
        {
            MyLifeEventTypeIds.BolusSquare => MyLifeBolusTypes.Square,
            MyLifeEventTypeIds.BolusDual => MyLifeBolusTypes.Dual,
            _ => MyLifeBolusTypes.Normal
        };

        if (carbs is > 0)
        {
            treatment.Carbs = carbs.Value;
        }

        if (isCalculated && info != null)
        {
            try
            {
                treatment.Notes = JsonSerializer.Serialize(
                    info.Value,
                    new JsonSerializerOptions { WriteIndented = true }
                );
            }
            catch (JsonException)
            {
                treatment.Notes = info.Value.GetRawText();
            }

            try
            {
                treatment.BolusCalc = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    info.Value.GetRawText()
                );
            }
            catch (JsonException)
            {
                treatment.BolusCalc = null;
            }
        }

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.DurationInMinutes, out var duration))
        {
            treatment.Duration = duration;
        }

        return [treatment];
    }
}