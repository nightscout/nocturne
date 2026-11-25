using System.Text.Json;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

/// <summary>
/// Handles MyLife profile switch indication events, creating DeviceEvent records.
/// </summary>
internal sealed class ProfileSwitchHandler : IMyLifeHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        if (ev.EventTypeId != MyLifeEventType.Indication)
            return false;

        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (info == null)
            return false;

        if (!info.Value.TryGetProperty(MyLifeJsonKeys.Key, out var keyElement))
            return false;

        if (keyElement.ValueKind != JsonValueKind.String)
            return false;

        var key = keyElement.GetString();
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(
                key,
                MyLifeJsonKeys.IndicationBasalProfileXChanged,
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                key,
                MyLifeJsonKeys.IndicationBasalProfileChanged,
                StringComparison.OrdinalIgnoreCase
            );
    }

    public IEnumerable<IV4Record> Handle(MyLifeEvent ev, MyLifeContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var profileName = ExtractProfileName(info);

        var notes = ev.InformationFromDevice;
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            notes = $"Profile: {profileName}";
        }

        var deviceEvent = MyLifeFactory.CreateDeviceEvent(ev, DeviceEventType.ProfileSwitch, notes);
        return [deviceEvent];
    }

    private static string? ExtractProfileName(JsonElement? info)
    {
        if (info is not { } element)
            return null;

        if (
            !element.TryGetProperty(MyLifeJsonKeys.Key, out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
        )
            return null;

        var key = keyElement.GetString();
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var parameterKey = key switch
        {
            _
                when string.Equals(
                    key,
                    MyLifeJsonKeys.IndicationBasalProfileXChanged,
                    StringComparison.OrdinalIgnoreCase
                ) => MyLifeJsonKeys.Parameter0,
            _
                when string.Equals(
                    key,
                    MyLifeJsonKeys.IndicationBasalProfileChanged,
                    StringComparison.OrdinalIgnoreCase
                ) => MyLifeJsonKeys.Parameter1,
            _ => null,
        };

        if (parameterKey == null)
            return null;

        if (
            !element.TryGetProperty(parameterKey, out var profileElement)
            || profileElement.ValueKind != JsonValueKind.String
        )
            return null;

        return profileElement.GetString();
    }
}
