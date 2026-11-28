using System.Reflection;
using System.Runtime.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// The <see cref="EnumMemberAttribute"/> value of each member of <typeparamref name="TEnum"/>, read
/// once, for a call site that stores or sends the wire string without a serialiser. A member with
/// no attribute is its own name.
/// </summary>
public static class EnumWireNames<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = typeof(TEnum)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .GroupBy(f => (TEnum)f.GetValue(null)!)
        .ToDictionary(
            g => g.Key,
            g => g.Last().GetCustomAttribute<EnumMemberAttribute>()?.Value ?? g.Last().Name);

    public static string Of(TEnum value) =>
        Names.TryGetValue(value, out var name) ? name : value.ToString();
}
