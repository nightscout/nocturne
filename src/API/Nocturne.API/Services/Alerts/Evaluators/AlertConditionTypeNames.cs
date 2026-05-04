using System.Reflection;
using System.Runtime.Serialization;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Caches the wire-format string for each <see cref="AlertConditionType"/> value (the
/// <see cref="EnumMemberAttribute"/> value, e.g. <c>"composite"</c>, <c>"rate_of_change"</c>,
/// <c>"loop_stale"</c>, <c>"pump_suspended"</c>).
/// Avoids per-call reflection in hot paths like the alert orchestrator and the evaluator registry.
/// New values added to <see cref="AlertConditionType"/> are picked up automatically as long as
/// they carry <see cref="EnumMemberAttribute"/>.
/// </summary>
internal static class AlertConditionTypeNames
{
    /// <summary>
    /// Reserved <see cref="SensorContext.CurrentPath"/> roots used by non-rule-body evaluation
    /// scopes (auto-resolve, smart-snooze conditions). Sustained-condition timer rows are keyed
    /// by <c>(ruleId, currentPath)</c>; these prefixes prevent rule-body timers from colliding
    /// with auxiliary-scope timers that also walk the same condition tree shape. Adding a new
    /// AlertConditionType with one of these names would silently share timer rows — guard at
    /// build time if/when more aux scopes are added.
    /// </summary>
    public const string AutoResolvePathRoot = "auto_resolve";
    public const string SnoozePathRoot = "snooze";

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
