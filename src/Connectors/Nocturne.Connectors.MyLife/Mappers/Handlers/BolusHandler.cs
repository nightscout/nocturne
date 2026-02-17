using System.Text.Json;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife bolus events, creating Bolus records and optionally
/// linked CarbIntake and BolusCalculation records when applicable.
/// </summary>
internal sealed class BolusHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId
            is MyLifeEventType.BolusNormal
                or MyLifeEventType.BolusSquare
                or MyLifeEventType.BolusDual;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (
            !MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.AmountOfBolus,
                out var insulin
            )
        )
            return [];

        var isCalculated = MyLifeMapperHelpers.IsCalculatedBolus(info);
        var carbs = MyLifeMapperHelpers.ResolveBolusCarbs(info);

        // Check for consolidated carbs from nearby carb correction events
        if (
            context.BolusCarbMatches.TryGetValue(
                MyLifeMapperHelpers.BuildEventKey(ev),
                out var matchedCarbs
            )
        )
        {
            carbs = matchedCarbs;
        }

        var hasCarbs = carbs is > 0;
        var hasInsulin = insulin > 0;

        // Generate a correlation ID to link related records
        Guid? correlationId = (hasCarbs || isCalculated) ? Guid.CreateVersion7() : null;

        var bolusType = ev.EventTypeId switch
        {
            MyLifeEventType.BolusSquare => BolusType.Square,
            MyLifeEventType.BolusDual => BolusType.Dual,
            _ => BolusType.Normal,
        };

        double? duration = null;
        if (
            MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.DurationInMinutes,
                out var durationVal
            )
        )
            duration = durationVal;

        var results = new List<IV4Record>();

        // Create Bolus record if there's insulin
        if (hasInsulin)
        {
            var bolus = MyLifeFactory.CreateBolus(ev, insulin, bolusType, duration, correlationId);
            results.Add(bolus);
        }

        // Create CarbIntake record if there are carbs
        if (hasCarbs)
        {
            var carbIntake = MyLifeFactory.CreateCarbIntake(ev, carbs!.Value, correlationId);
            results.Add(carbIntake);
        }

        // Create BolusCalculation record if this was a calculated bolus
        if (isCalculated && info.HasValue)
        {
            var bolusCalc = CreateBolusCalculation(ev, info.Value, correlationId);
            if (bolusCalc != null)
                results.Add(bolusCalc);
        }

        return results;
    }

    private static BolusCalculation? CreateBolusCalculation(
        MyLifeEvent ev,
        JsonElement info,
        Guid? correlationId
    )
    {
        double? bgInput = null;
        double? carbInput = null;
        double? iob = null;
        double? insulinRec = null;

        // Extract calculator inputs from the info JSON
        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.CalcBg, out var bg))
            bgInput = bg;

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.CalcCarbs, out var carbs))
            carbInput = carbs;

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.CalcIob, out var iobVal))
            iob = iobVal;

        if (
            MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.SuggestedMealBolus,
                out var suggested
            )
        )
            insulinRec = suggested;

        // Only create a calculation record if we have meaningful input
        if (bgInput == null && carbInput == null && insulinRec == null)
            return null;

        return MyLifeFactory.CreateBolusCalculation(
            ev,
            bgInput,
            carbInput,
            iob,
            insulinRec,
            correlationId
        );
    }
}
