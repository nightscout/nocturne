using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Conditions;

/// <summary>
/// The wire-format string of each <see cref="AlertConditionType"/> value, e.g. <c>"composite"</c>,
/// <c>"rate_of_change"</c>, <c>"loop_stale"</c>, <c>"pump_suspended"</c>, as
/// <see cref="EnumWireNames{TEnum}"/> reads it, and the reverse lookup.
/// </summary>
internal static class AlertConditionTypeNames
{
    /// <summary>
    /// Reserved condition-path roots used by non-rule-body evaluation scopes (auto-resolve,
    /// smart-snooze conditions). Sustained-condition timer rows are keyed by
    /// <c>(ruleId, path)</c>; these prefixes prevent rule-body timers from colliding
    /// with auxiliary-scope timers that also walk the same condition tree shape. Adding a new
    /// AlertConditionType with one of these names would silently share timer rows — guard at
    /// build time if/when more aux scopes are added.
    /// </summary>
    public const string AutoResolvePathRoot = "auto_resolve";
    public const string SnoozePathRoot = "snooze";

    private static readonly Dictionary<string, AlertConditionType> FromWire = Enum.GetValues<AlertConditionType>()
        .ToDictionary(EnumWireNames<AlertConditionType>.Of, type => type, StringComparer.Ordinal);

    /// <summary>
    /// Returns the snake_case wire string for the given <paramref name="type"/>.
    /// </summary>
    public static string ToWireString(AlertConditionType type) => EnumWireNames<AlertConditionType>.Of(type);

    /// <summary>
    /// The <see cref="AlertConditionType"/> whose wire string is exactly <paramref name="wire"/>, or null.
    /// </summary>
    public static AlertConditionType? FromWireString(string wire) =>
        FromWire.TryGetValue(wire, out var t) ? t : null;
}
