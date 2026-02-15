using System.Text.Json;
using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Mappers.Helpers;
using Nocturne.Connectors.MyLife.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Handlers;

internal sealed class ProfileSwitchTreatmentHandler : IMyLifeTreatmentHandler
{
    public bool CanHandle(MyLifeEvent ev)
    {
        if (ev.EventTypeId != MyLifeEventTypeIds.Indication) return false;

        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        if (info == null) return false;

        if (!info.Value.TryGetProperty(MyLifeJsonKeys.Key, out var keyElement)) return false;

        if (keyElement.ValueKind != JsonValueKind.String) return false;

        var key = keyElement.GetString();
        if (string.IsNullOrWhiteSpace(key)) return false;

        if (
            string.Equals(
                key,
                MyLifeJsonKeys.IndicationBasalProfileXChanged,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return true;

        if (
            string.Equals(
                key,
                MyLifeJsonKeys.IndicationBasalProfileChanged,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return true;

        return false;
    }

    public IEnumerable<Treatment> Handle(MyLifeEvent ev, MyLifeTreatmentContext context)
    {
        var info = MyLifeMapperHelpers.ParseInfo(ev.InformationFromDevice);
        var profileSwitch = MyLifeTreatmentFactory.CreateWithSuffix(
            ev,
            MyLifeTreatmentTypes.ProfileSwitch,
            MyLifeIdSuffixes.ProfileSwitch
        );
        profileSwitch.Notes = ev.InformationFromDevice;

        var profile = ExtractProfileName(info);
        if (!string.IsNullOrWhiteSpace(profile)) profileSwitch.Profile = profile;

        return [profileSwitch];
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
            _ => null
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