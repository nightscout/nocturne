using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
///     Handler for MyLife Basal events (event ID 22) - Basal insulin amount delivered.
///     These events report actual insulin delivery amounts, typically from non-loop pumps.
///     Produces BasalDelivery StateSpans only - basal treatments are synthesized on-demand from v1-v3 endpoints.
/// </summary>
internal sealed class BasalAmountTreatmentHandler : IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.Basal;
    }

    public IEnumerable<StateSpan> HandleStateSpan(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        // Basal amount events (event ID 22) report delivered insulin amounts.
        // We need to convert this to a rate. Since we don't know the exact duration,
        // we assume a standard reporting interval. The StateSpan duration will be
        // calculated during post-processing when the next span arrives.
        //
        // For now, we use the insulin amount as an approximation.
        // In typical MyLife data, these events happen once per hour or less frequently.
        // The actual rate calculation will need the duration from the next event.

        if (!MyLifeMapperHelpers.TryParseDouble(ev.Value, out var insulin)) return [];

        // Since these are typically hourly delivery amounts, we can estimate the rate
        // as equal to the insulin value (U delivered in ~1 hour = U/h)
        // This is an approximation - the precise rate will be determined by the
        // overall basal delivery timeline.
        var estimatedRate = insulin;

        // Origin is "Scheduled" for basal amount events - these come from the
        // pump's programmed basal schedule, not from algorithm adjustments
        var origin = BasalDeliveryOrigin.Scheduled;

        // Check if rate is 0 (suspended)
        if (estimatedRate <= 0) origin = BasalDeliveryOrigin.Suspended;

        var stateSpan = MyLifeStateSpanFactory.CreateBasalDelivery(ev, estimatedRate, origin);
        return [stateSpan];
    }
}