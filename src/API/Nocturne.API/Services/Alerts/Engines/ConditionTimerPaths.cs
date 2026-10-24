using System.Text.Json;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The paths a rule's sustained timers can live at: every node of each condition tree the rule
/// stores, under the root it is evaluated with (docs/alerts/engine-semantics.md §2.3), whether or
/// not the rule or the tree is enabled.
/// </summary>
internal static class ConditionTimerPaths
{
    /// <summary>
    /// The paths of <paramref name="rule"/>'s body, auto-resolve and smart-snooze trees. A tree
    /// that does not parse, as JSON or for the engine, is never evaluated, so it keeps none.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The engine's answer could not be read.</exception>
    public static HashSet<string> Of(AlertRule rule)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        var wire = AlertConditionTypeNames.ToWireString(rule.ConditionType);
        Add(paths, () => RustEnvelopeMapper.ParseJson(RustEnvelopeMapper.WrapPayload(wire, rule.ConditionParams)), wire);

        if (!string.IsNullOrWhiteSpace(rule.AutoResolveParams))
            Add(paths, () => RustEnvelopeMapper.ParseJson(rule.AutoResolveParams), AlertConditionTypeNames.AutoResolvePathRoot);

        if (SmartSnoozeConfig.Parse(rule.ClientConfiguration).Conditions is { } conditions)
            Add(paths, () => SmartSnoozeConfig.ConditionTree(conditions), AlertConditionTypeNames.SnoozePathRoot);

        return paths;
    }

    private static void Add(HashSet<string> paths, Func<JsonElement> tree, string root)
    {
        try
        {
            paths.UnionWith(RustAlertEngine.LeafPaths(tree(), root).Paths!);
        }
        catch (JsonException)
        {
        }
        catch (RustAlertEngineException ex) when (ex.IsConditionRejection)
        {
        }
    }
}
