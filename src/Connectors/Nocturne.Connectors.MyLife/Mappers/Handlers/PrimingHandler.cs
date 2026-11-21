using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife priming events, creating DeviceEvent records.
/// Needle priming also creates an additional SiteChange event.
/// </summary>
internal sealed class PrimingHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        return ev.EventTypeId
            is MyLifeEventType.Priming
                or MyLifeEventType.TubePriming
                or MyLifeEventType.NeedlePriming;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        var eventType = ev.EventTypeId switch
        {
            MyLifeEventType.TubePriming => DeviceEventType.TubePriming,
            MyLifeEventType.NeedlePriming => DeviceEventType.NeedlePriming,
            _ => DeviceEventType.Priming,
        };

        var notes = ev.InformationFromDevice;

        // Add priming amount to notes if available
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (
            MyLifeMapperHelpers.TryGetInfoDouble(info, MyLifeJsonKeys.PrimingAmount, out var amount)
        )
        {
            notes = $"{notes} (Priming amount: {amount}U)";
        }

        var results = new List<IV4Record> { MyLifeFactory.CreateDeviceEvent(ev, eventType, notes) };

        // Needle priming implies a site change
        if (ev.EventTypeId == MyLifeEventType.NeedlePriming)
        {
            var siteChange = MyLifeFactory.CreateDeviceEvent(
                ev,
                DeviceEventType.SiteChange,
                ev.InformationFromDevice
            );
            // Use a different legacy ID suffix for the site change
            siteChange.LegacyId = $"{siteChange.LegacyId}-sitechange";
            results.Add(siteChange);
        }

        return results;
    }
}
