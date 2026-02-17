using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife indication events, creating DeviceEvent or Note records.
/// Battery removal indications become PumpBatteryChange device events.
/// </summary>
internal sealed class IndicationHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        // Indication events that are handled by ProfileSwitchHandler should not be handled here
        if (ev.EventTypeId != MyLifeEventTypeIds.Indication)
            return false;

        // Check if this is a profile switch indication (handled separately)
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (IsProfileSwitchIndication(info))
            return false;

        return true;
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var notes = ev.InformationFromDevice;

        // Battery removal becomes a device event
        if (MyLifeMapperHelpers.IsBatteryRemovedIndication(info))
        {
            var deviceEvent = MyLifeFactory.CreateDeviceEvent(
                ev,
                DeviceEventType.PumpBatteryChange,
                notes
            );
            return [deviceEvent];
        }

        // Other indications become notes
        var note = MyLifeFactory.CreateNote(ev, notes ?? string.Empty, "Indication");
        return [note];
    }

    private static bool IsProfileSwitchIndication(System.Text.Json.JsonElement? info)
    {
        if (info == null)
            return false;

        if (!info.Value.TryGetProperty("KEY", out var keyElement))
            return false;

        if (keyElement.ValueKind != System.Text.Json.JsonValueKind.String)
            return false;

        var key = keyElement.GetString();
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(
                key,
                "IndicationBasalProfileXChanged",
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                key,
                "IndicationBasalProfileChanged",
                StringComparison.OrdinalIgnoreCase
            );
    }
}
