using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Filters a batch of <see cref="AlertRuleSnapshot"/>s down to those whose <c>alert_state</c>
/// references all resolve to other rules in the same enabled set. A rule that references a
/// disabled or deleted parent is dropped from evaluation — the parent isn't being evaluated,
/// so its state would be stale and the chained rule can't fire meaningfully.
/// </summary>
/// <remarks>
/// Only the immediate enabled-set is checked. A reference to a rule that itself was filtered
/// out earlier in this pass (e.g. transitively chained through a disabled grandparent) is
/// detected on the next pass once that intermediate rule is filtered: the chained child's
/// reference no longer resolves into the surviving set.
///
/// Cycle detection (<c>alertId</c> equals the rule's own id) is enforced at write time by
/// <c>AlertReferenceService</c>; here we treat such a self-reference as evaluable, leaving the
/// runtime <c>AlertStateEvaluator</c> to handle it via the live snapshot it already has access to.
/// </remarks>
public static class RuleReferenceResolver
{
    /// <summary>
    /// Returns the subset of <paramref name="rules"/> that have no <c>alert_state</c> references
    /// to rule ids outside the input set. Order is preserved.
    /// </summary>
    public static IReadOnlyList<AlertRuleSnapshot> FilterEvaluable(IReadOnlyList<AlertRuleSnapshot> rules)
    {
        if (rules.Count == 0) return rules;

        var enabledIds = new HashSet<Guid>(rules.Count);
        foreach (var r in rules) enabledIds.Add(r.Id);

        var keep = new List<AlertRuleSnapshot>(rules.Count);
        foreach (var rule in rules)
        {
            if (AllReferencesResolve(rule, enabledIds))
                keep.Add(rule);
        }
        return keep;
    }

    private static bool AllReferencesResolve(AlertRuleSnapshot rule, HashSet<Guid> enabledIds)
    {
        switch (rule.ConditionType)
        {
            case AlertConditionType.AlertState:
                {
                    var c = TryDeserialize<AlertStateCondition>(rule.ConditionParams);
                    return c is null || c.AlertId == rule.Id || enabledIds.Contains(c.AlertId);
                }
            case AlertConditionType.Composite:
                {
                    var composite = TryDeserialize<CompositeCondition>(rule.ConditionParams);
                    if (composite is null) return true;
                    foreach (var child in composite.Conditions)
                        if (!NodeReferencesResolve(child, rule.Id, enabledIds)) return false;
                    return true;
                }
            case AlertConditionType.Not:
                {
                    var not = TryDeserialize<NotCondition>(rule.ConditionParams);
                    return not is null || NodeReferencesResolve(not.Child, rule.Id, enabledIds);
                }
            case AlertConditionType.Sustained:
                {
                    var sustained = TryDeserialize<SustainedCondition>(rule.ConditionParams);
                    return sustained is null || NodeReferencesResolve(sustained.Child, rule.Id, enabledIds);
                }
            default:
                return true;
        }
    }

    private static bool NodeReferencesResolve(ConditionNode node, Guid ownerRuleId, HashSet<Guid> enabledIds)
    {
        var unresolved = ConditionPath.Walk<UnresolvedMarker>(node, (visited, _) =>
        {
            if (string.Equals(visited.Type, "alert_state", StringComparison.OrdinalIgnoreCase) &&
                visited.AlertState is { } state &&
                state.AlertId != ownerRuleId &&
                !enabledIds.Contains(state.AlertId))
            {
                return UnresolvedMarker.Instance;
            }
            return null;
        });

        return unresolved is null;
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, EvaluatorJson.Options); }
        catch (JsonException) { return null; }
    }

    private sealed class UnresolvedMarker
    {
        public static readonly UnresolvedMarker Instance = new();
    }
}
