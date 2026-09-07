using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
///     Handler for MyLife TempBasal events (event ID 4) - Temporary basal rate program.
///     These events represent user-initiated temporary basal programs (not algorithm-adjusted).
///     Produces TempBasal records.
/// </summary>
internal sealed class TempBasalHandler : IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventType.TempBasal;
    }

    public IEnumerable<TempBasal> HandleStateSpan(MyLifeEvent ev, MyLifeContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);

        // Try to get the rate - this event type can have either percentage or absolute rate
        double rate = 0;
        if (
            MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.ValueInUperH,
                out var absoluteRate
            )
        )
            rate = absoluteRate;

        // TempBasal events (event ID 4) are user-initiated temporary basal programs, so the origin is
        // Manual whatever the rate. A 0 U/h or 0 % program is still a temp basal the user set, not a
        // suspension - mylife reports suspension as its own PumpSuspend/PumpResume event pair.
        var origin = TempBasalOrigin.Manual;

        var tempBasal = MyLifeStateSpanFactory.CreateTempBasal(ev, rate, origin);

        // For user-initiated temp basals, we can calculate the end time
        // since the duration is explicit in the event
        if (
            MyLifeMapperHelpers.TryGetInfoDouble(
                info,
                MyLifeJsonKeys.Minutes,
                out var durationMinutes
            )
        )
        {
            tempBasal.EndTimestamp = tempBasal.StartTimestamp.AddMinutes(durationMinutes);
        }

        return [tempBasal];
    }
}
