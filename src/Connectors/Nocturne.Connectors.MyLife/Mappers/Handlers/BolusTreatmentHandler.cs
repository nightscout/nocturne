using System.Text.Json;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class BolusTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId
            is MyLifeEventTypeIds.BolusNormal
                or MyLifeEventTypeIds.BolusSquare
                or MyLifeEventTypeIds.BolusDual;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (
            !MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.AmountOfBolus,
                out var insulin
            )
        )
        {
            return [];
        }

        var isCalculated = MyLifeMapperHelpers.IsCalculatedBolus(info);
        var carbs = MyLifeMapperHelpers.ResolveBolusCarbs(info);
        if (
            context.BolusCarbMatches.TryGetValue(
                MyLifeMapperHelpers.BuildEventKey(ev),
                out var matchedCarbs
            )
        )
        {
            carbs = matchedCarbs;
        }

        // Use consistent classification logic from shared TreatmentTypes
        // Classification rules:
        // - Carbs > 0 AND Insulin > 0 → Meal Bolus
        // - Carbs > 0 AND Insulin ≤ 0 → Carb Correction
        // - Carbs ≤ 0 AND Insulin > 0 → Correction Bolus
        var hasCarbs = carbs is > 0;
        var hasInsulin = insulin > 0;
        var eventType = (hasCarbs, hasInsulin) switch
        {
            (true, true) => TreatmentTypes.MealBolus,
            (true, false) => TreatmentTypes.CarbCorrection,
            _ => TreatmentTypes.CorrectionBolus
        };

        var treatment = MyLifeTreatmentFactory.Create(ev, eventType);

        treatment.Insulin = insulin;
        treatment.BolusType = ev.EventTypeId switch
        {
            MyLifeEventTypeIds.BolusSquare => MyLifeBolusTypes.Square,
            MyLifeEventTypeIds.BolusDual => MyLifeBolusTypes.Dual,
            _ => MyLifeBolusTypes.Normal,
        };

        if (carbs is > 0)
        {
            treatment.Carbs = carbs.Value;
        }

        if (isCalculated && info.HasValue)
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

        if (
            MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.DurationInMinutes,
                out var duration
            )
        )
        {
            treatment.Duration = duration;
        }

        return [treatment];
    }
}
