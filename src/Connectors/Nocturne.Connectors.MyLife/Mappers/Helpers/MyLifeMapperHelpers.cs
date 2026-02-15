using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Mappers.Constants;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Mappers.Helpers;

internal static class MyLifeMapperHelpers
{
    internal static bool TryParseDouble(string? value, out double result)
    {
        return double.TryParse(
            value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out result
        );
    }

    internal static DateTimeOffset FromInstantTicks(long ticks)
    {
        var milliseconds = ticks / 10_000;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    internal static long ToUnixMilliseconds(long ticks)
    {
        return FromInstantTicks(ticks).ToUnixTimeMilliseconds();
    }

    internal static string BuildEventKey(MyLifeEvent ev)
    {
        var builder = new StringBuilder();
        builder.Append(ev.EventTypeId).Append('|');
        builder.Append(ev.EventDateTime).Append('|');
        if (!string.IsNullOrWhiteSpace(ev.Value)) builder.Append(ev.Value);

        builder.Append('|');
        if (!string.IsNullOrWhiteSpace(ev.InformationFromDevice)) builder.Append(ev.InformationFromDevice);

        builder.Append('|');
        if (!string.IsNullOrWhiteSpace(ev.PatientId)) builder.Append(ev.PatientId);

        builder.Append('|');
        if (!string.IsNullOrWhiteSpace(ev.DeviceId)) builder.Append(ev.DeviceId);

        builder.Append('|');
        if (ev.IndexOnDevice.HasValue) builder.Append(ev.IndexOnDevice.Value);

        builder.Append('|');
        builder.Append(ev.CRC32Checksum);

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool IsBatteryRemovedIndication(JsonElement? info)
    {
        if (info == null) return false;

        if (!info.Value.TryGetProperty(MyLifeJsonKeys.Key, out var element)) return false;

        if (element.ValueKind != JsonValueKind.String) return false;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value)) return false;

        return string.Equals(
            value,
            MyLifeJsonKeys.IndicationBatteryRemoved,
            StringComparison.OrdinalIgnoreCase
        );
    }

    internal static JsonElement? ParseInfo(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;

        try
        {
            return JsonDocument.Parse(info).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool TryGetInfoDouble(JsonElement? info, string name, out double value)
    {
        value = 0;
        if (info == null) return false;

        if (!info.Value.TryGetProperty(name, out var element)) return false;

        return element.ValueKind switch
        {
            JsonValueKind.String => TryParseDouble(element.GetString(), out value),
            JsonValueKind.Number => element.TryGetDouble(out value),
            _ => false
        };
    }

    internal static bool TryGetInfoBool(JsonElement? info, string name)
    {
        if (info == null) return false;

        if (!info.Value.TryGetProperty(name, out var element)) return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
        }

        if (element.ValueKind != JsonValueKind.String) return false;
        var value = element.GetString();
        return string.Equals(value, MyLifeBooleanStrings.True, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCalculatedBolus(JsonElement? info)
    {
        if (info == null) return false;

        if (TryGetInfoBool(info, MyLifeJsonKeys.BolusIsCalculated)) return true;

        if (TryGetInfoDouble(info, MyLifeJsonKeys.CalcCarbs, out var carbs) && carbs > 0) return true;

        return TryGetInfoDouble(info, MyLifeJsonKeys.SuggestedMealBolus, out var suggested) && suggested > 0;
    }

    internal static double? ResolveBolusCarbs(JsonElement? info)
    {
        if (TryGetInfoDouble(info, MyLifeJsonKeys.CalcCarbs, out var carbs)) return carbs;

        if (TryGetInfoDouble(info, MyLifeJsonKeys.Carbs, out var carbsAlt)) return carbsAlt;

        if (TryGetInfoDouble(info, MyLifeJsonKeys.CalcCarb, out var carbsSingle)) return carbsSingle;

        return null;
    }
}