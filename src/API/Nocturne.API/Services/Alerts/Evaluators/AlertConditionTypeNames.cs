using System.Reflection;
using System.Runtime.Serialization;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Caches the wire-format string for each <see cref="AlertConditionType"/> value (the
/// <see cref="EnumMemberAttribute"/> value, e.g. <c>"composite"</c>, <c>"rate_of_change"</c>).
/// Avoids per-call reflection in hot paths like the alert orchestrator and the evaluator registry.
/// </summary>
internal static class AlertConditionTypeNames
{
    private static readonly Dictionary<AlertConditionType, string> ToWire = BuildToWire();
    private static readonly Dictionary<string, AlertConditionType> FromWire =
        ToWire.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the snake_case wire string for the given <paramref name="type"/>.
    /// </summary>
    public static string ToWireString(AlertConditionType type) =>
        ToWire.TryGetValue(type, out var s) ? s : type.ToString().ToLowerInvariant();

    /// <summary>
    /// Resolves a wire string back to its <see cref="AlertConditionType"/>, or returns null if unknown.
    /// </summary>
    public static AlertConditionType? FromWireString(string wire) =>
        FromWire.TryGetValue(wire, out var t) ? t : null;

    private static Dictionary<AlertConditionType, string> BuildToWire()
    {
        var values = Enum.GetValues<AlertConditionType>();
        var map = new Dictionary<AlertConditionType, string>(values.Length);
        foreach (var value in values)
        {
            var member = typeof(AlertConditionType).GetMember(value.ToString()).FirstOrDefault();
            var attr = member?.GetCustomAttribute<EnumMemberAttribute>();
            map[value] = attr?.Value ?? value.ToString().ToLowerInvariant();
        }
        return map;
    }
}
