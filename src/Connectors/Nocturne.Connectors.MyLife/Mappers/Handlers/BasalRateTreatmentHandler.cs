using Nocturne.Connectors.MyLife.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handler for MyLife BasalRate events (event ID 17) - Pump program basal rate changes.
/// These events report the current basal rate being delivered by the pump.
/// The IsTempBasalRate flag indicates if this is an algorithm-adjusted rate (CamAPS).
/// Produces BasalDelivery StateSpans only - basal treatments are synthesized on-demand from v1-v3 endpoints.
/// </summary>
internal sealed class BasalRateTreatmentHandler : IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.BasalRate;
    }

    public IEnumerable<StateSpan> HandleStateSpan(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (!MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.BasalRate, out var rate))
        {
            return [];
        }

        var isTemp = MyLifeMapperHelpers.TryGetInfoBool(info, MyLifeJsonKeys.IsTempBasalRate);

        // Determine origin based on the event context:
        // - IsTempBasalRate = true means algorithm adjusted (CamAPS, Loop, etc.)
        // - IsTempBasalRate = false means scheduled basal from pump profile
        // - Rate = 0 indicates suspended delivery
        BasalDeliveryOrigin origin;
        if (rate <= 0)
        {
            origin = BasalDeliveryOrigin.Suspended;
        }
        else if (isTemp)
        {
            // IsTempBasalRate = true indicates algorithm adjustment (e.g., CamAPS)
            origin = BasalDeliveryOrigin.Algorithm;
        }
        else
        {
            // Regular basal rate from pump schedule
            origin = BasalDeliveryOrigin.Scheduled;
        }

        var stateSpan = MyLifeStateSpanFactory.CreateBasalDelivery(ev, rate, origin);
        return [stateSpan];
    }
}
