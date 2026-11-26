using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nocturne.Infrastructure.Data.Converters;

/// <summary>
/// EF Core value converter that maps enum values to/from their
/// <see cref="EnumMemberAttribute.Value"/> strings. Falls back to
/// <c>ToString()</c> if the attribute is missing.
/// </summary>
/// <param name="tolerant">
/// Reads a value no member has as <see cref="Unreadable"/> instead of throwing, for a column whose
/// readers turn it into a member through <c>AlertRuleMapper</c>. A strict column throws, which
/// fails the whole read.
/// </param>
internal sealed class EnumMemberValueConverter<TEnum>(bool tolerant = false)
    : ValueConverter<TEnum, string>(
        v => ToProvider(v),
        Reader(tolerant)), IUnreadableValues
    where TEnum : struct, Enum
{
    /// <summary>
    /// What a tolerant column reads a stored value no member has as. No member has it, so
    /// <see cref="Enum.IsDefined{TEnum}(TEnum)"/> tells it apart.
    /// </summary>
    public static readonly TEnum Unreadable = (TEnum)Enum.ToObject(typeof(TEnum), -1);

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

    public bool IsUnreadable(object? value) => tolerant && value is TEnum e && !Enum.IsDefined(e);

    private static Expression<Func<string, TEnum>> Reader(bool tolerant) =>
        tolerant ? v => ReadOrUnreadable(v) : v => FromProvider(v);

    private static TEnum FromProvider(string value) =>
        FromStringMap.TryGetValue(value, out var e) ? e : Enum.Parse<TEnum>(value, ignoreCase: true);

    private static TEnum ReadOrUnreadable(string value) =>
        FromStringMap.TryGetValue(value, out var e) ? e : Unreadable;
}

/// <summary>A converter that can read a stored value as one no member has.</summary>
internal interface IUnreadableValues
{
    /// <summary>Whether <paramref name="value"/> is what the converter read a value no member has as.</summary>
    bool IsUnreadable(object? value);
}
