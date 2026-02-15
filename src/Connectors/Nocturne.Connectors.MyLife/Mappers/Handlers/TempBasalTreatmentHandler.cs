using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
///     Handler for MyLife TempBasal events (event ID 4) - Temporary basal rate program.
///     These events represent user-initiated temporary basal programs (not algorithm-adjusted).
///     Produces both Treatment records (for backward compatibility) and BasalDelivery StateSpans.
/// </summary>
internal sealed class TempBasalTreatmentHandler : IMyLifeTreatmentHandler, IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.TempBasal;
    }

    public IEnumerable<StateSpan> HandleStateSpan(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);

        // Try to get the rate - this event type can have either percentage or absolute rate
        double rate = 0;
        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.ValueInUperH, out var absoluteRate))
            rate = absoluteRate;

        // TempBasal events (event ID 4) are user-initiated temporary basal programs.
        // This is different from IsTempBasalRate which indicates algorithm adjustments.
        // Origin is "Manual" for user-initiated temp basal programs.
        BasalDeliveryOrigin origin;
        if (rate <= 0)
        {
            // Zero rate or percentage indicates suspended delivery
            // (though percentage-based would be relative to scheduled rate)
            if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Percentage, out var percent) && percent <= 0)
                origin = BasalDeliveryOrigin.Suspended;
            else if (rate <= 0 && !MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Percentage, out _))
                origin = BasalDeliveryOrigin.Suspended;
            else
                origin = BasalDeliveryOrigin.Manual;
        }
        else
        {
            // User-initiated temporary basal rate
            origin = BasalDeliveryOrigin.Manual;
        }

        var stateSpan = MyLifeStateSpanFactory.CreateBasalDelivery(ev, rate, origin);

        // Include additional metadata for TempBasal events
        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Percentage, out var percentValue))
            stateSpan.Metadata!["percent"] = percentValue;

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Minutes, out var durationMinutes))
        {
            stateSpan.Metadata!["durationMinutes"] = durationMinutes;

            // For user-initiated temp basals, we can calculate the end time
            // since the duration is explicit in the event
            var startMills = stateSpan.StartMills;
            var endMills = startMills + (long)(durationMinutes * 60 * 1000);
            stateSpan.EndMills = endMills;
        }

        return [stateSpan];
    }

    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventTypeIds.TempBasal;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var treatment = MyLifeTreatmentFactory.Create(ev, MyLifeTreatmentTypes.TempBasal);
        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Percentage, out var percent))
            treatment.Percent = percent;

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.Minutes, out var minutes))
            treatment.Duration = minutes;

        if (MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.ValueInUperH, out var rate))
            treatment.Rate = rate;

        if (!context.TryRegisterTempBasal(treatment.Mills)) return [];

        if (context.TryGetTempBasalRate(treatment.Mills, out var consolidatedRate)) treatment.Rate = consolidatedRate;

        return [treatment];
    }
}