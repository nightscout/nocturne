using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife device events (site changes, pod events, pump suspend/resume, etc.),
/// creating DeviceEvent records.
/// </summary>
internal sealed class DeviceEventHandler : IMyLifeHandler
{
    private static readonly Dictionary<int, DeviceEventType> EventTypeMapping = new()
    {
        { MyLifeEventTypeIds.PodActivated, DeviceEventType.PodActivated },
        { MyLifeEventTypeIds.PodDeactivated, DeviceEventType.PodDeactivated },
        { MyLifeEventTypeIds.PumpSuspend, DeviceEventType.PumpSuspend },
        { MyLifeEventTypeIds.PumpResume, DeviceEventType.PumpResume },
        { MyLifeEventTypeIds.SiteChange, DeviceEventType.SiteChange },
        { MyLifeEventTypeIds.Rewind, DeviceEventType.Rewind },
        { MyLifeEventTypeIds.DateChanged, DeviceEventType.DateChanged },
        { MyLifeEventTypeIds.TimeChanged, DeviceEventType.TimeChanged },
        { MyLifeEventTypeIds.BolusMaxChanged, DeviceEventType.BolusMaxChanged },
        { MyLifeEventTypeIds.BasalMaxChanged, DeviceEventType.BasalMaxChanged },
    };

    public bool CanHandle(MyLifeEvent ev)
    {
        return EventTypeMapping.ContainsKey(ev.EventTypeId);
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        if (!EventTypeMapping.TryGetValue(ev.EventTypeId, out var eventType))
            return [];

        var notes = ev.InformationFromDevice;
        var deviceEvent = MyLifeFactory.CreateDeviceEvent(ev, eventType, notes);
        return [deviceEvent];
    }
}
