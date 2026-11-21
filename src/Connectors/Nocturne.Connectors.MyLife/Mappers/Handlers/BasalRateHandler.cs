using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
///     Handler for MyLife BasalRate events (event ID 17) - Pump program basal rate changes.
///     These events report the current basal rate being delivered by the pump.
///     The IsTempBasalRate flag indicates if this is an algorithm-adjusted rate (CamAPS).
///     Produces TempBasal records.
/// </summary>
internal sealed class BasalRateHandler : IMyLifeStateSpanHandler
{
    public bool CanHandleStateSpan(MyLifeEvent ev)
    {
        return ev.EventTypeId == MyLifeEventType.BasalRate;
    }

    public IEnumerable<TempBasal> HandleStateSpan(MyLifeEvent ev, MyLifeContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (!MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.BasalRate, out var rate)) return [];

        var isTemp = MyLifeMapperHelpers.TryGetInfoBool(info, MyLifeJsonKeys.IsTempBasalRate);

        // IsTempBasalRate = true is an algorithm adjustment (CamAPS), otherwise the rate comes from
        // the pump's programmed schedule. A zero rate is still a rate on either path: CamAPS commands
        // 0 U/h whenever it wants to withhold basal, and a profile segment can be programmed at 0 U/h.
        // Neither is a suspension - mylife reports that as its own PumpSuspend/PumpResume event pair,
        // which DeviceEventHandler maps.
        var origin = isTemp ? TempBasalOrigin.Algorithm : TempBasalOrigin.Scheduled;

        var tempBasal = MyLifeStateSpanFactory.CreateTempBasal(ev, rate, origin);
        return [tempBasal];
    }
}
