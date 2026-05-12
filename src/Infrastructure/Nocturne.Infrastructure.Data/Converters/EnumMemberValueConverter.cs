using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nocturne.Infrastructure.Data.Converters;

/// <summary>
/// EF Core value converter that maps enum values to/from their
/// <see cref="EnumMemberAttribute.Value"/> strings. Falls back to
/// <c>ToString()</c> if the attribute is missing.
/// </summary>
internal sealed class EnumMemberValueConverter<TEnum>()
    : ValueConverter<TEnum, string>(
        v => ToProvider(v),
        v => FromProvider(v))
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToStringMap = BuildToStringMap();
    private static readonly Dictionary<string, TEnum> FromStringMap = BuildFromStringMap();

    private static Dictionary<TEnum, string> BuildToStringMap()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            map[value] = attr?.Value ?? value.ToString();
        }
        return map;
    }

    private static Dictionary<string, TEnum> BuildFromStringMap()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (TEnum)field.GetValue(null)!;
            // Add both the member name and the EnumMember value so lookups
            // succeed even when attribute reflection fails at runtime.
            map.TryAdd(field.Name, value);
            var attr = field.GetCustomAttribute<EnumMemberAttribute>();
            if (attr?.Value is { } attrValue)
                map.TryAdd(attrValue, value);
        }
        // Also include whatever ToStringMap resolved (covers edge cases where
        // ToStringMap and FromStringMap disagree about attribute availability).
        foreach (var kv in ToStringMap)
            map.TryAdd(kv.Value, kv.Key);
        return map;
    }

    private static string ToProvider(TEnum value) =>
        ToStringMap.TryGetValue(value, out var s) ? s : value.ToString();

    private static TEnum FromProvider(string value) =>
        FromStringMap.TryGetValue(value, out var e) ? e : Enum.Parse<TEnum>(value, ignoreCase: true);
}
