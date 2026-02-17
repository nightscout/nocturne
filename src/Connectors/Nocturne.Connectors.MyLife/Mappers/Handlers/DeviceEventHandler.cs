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
    private static readonly Dictionary<MyLifeEventType, DeviceEventType> EventTypeMapping = new()
    {
        { MyLifeEventType.PodActivated, DeviceEventType.PodActivated },
        { MyLifeEventType.PodDeactivated, DeviceEventType.PodDeactivated },
        { MyLifeEventType.PumpSuspend, DeviceEventType.PumpSuspend },
        { MyLifeEventType.PumpResume, DeviceEventType.PumpResume },
        { MyLifeEventType.SiteChange, DeviceEventType.SiteChange },
        { MyLifeEventType.Rewind, DeviceEventType.Rewind },
        { MyLifeEventType.DateChanged, DeviceEventType.DateChanged },
        { MyLifeEventType.TimeChanged, DeviceEventType.TimeChanged },
        { MyLifeEventType.BolusMaxChanged, DeviceEventType.BolusMaxChanged },
        { MyLifeEventType.BasalMaxChanged, DeviceEventType.BasalMaxChanged },
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
