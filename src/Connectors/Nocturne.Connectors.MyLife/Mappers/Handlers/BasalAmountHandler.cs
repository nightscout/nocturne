using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
///     Handler for MyLife Basal events (event ID 22) - Basal insulin amount delivered.
///     These events report actual insulin delivery amounts, typically from non-loop pumps.
///     Produces TempBasal records.
/// </summary>
internal sealed class BasalAmountHandler : IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventType.Basal;
    }

    public IEnumerable<TempBasal> HandleStateSpan(MyLifeEvent ev, MyLifeContext context)
    {
        // Basal amount events (event ID 22) report delivered insulin amounts.
        // We need to convert this to a rate. Since we don't know the exact duration,
        // we assume a standard reporting interval. The TempBasal duration will be
        // calculated during post-processing when the next record arrives.
        //
        // For now, we use the insulin amount as an approximation.
        // In typical MyLife data, these events happen once per hour or less frequently.
        // The actual rate calculation will need the duration from the next event.

        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var insulin))
            return [];

        // Since these are typically hourly delivery amounts, we can estimate the rate
        // as equal to the insulin value (U delivered in ~1 hour = U/h)
        // This is an approximation - the precise rate will be determined by the
        // overall basal delivery timeline.
        var estimatedRate = insulin;

        // Basal amount events come from the pump's programmed schedule, not from algorithm
        // adjustments. An hour that delivered 0 U is a zero rate, not a suspension - mylife reports
        // suspension as its own PumpSuspend/PumpResume event pair.
        var origin = TempBasalOrigin.Scheduled;

        var tempBasal = MyLifeStateSpanFactory.CreateTempBasal(ev, estimatedRate, origin);
        return [tempBasal];
    }
}
