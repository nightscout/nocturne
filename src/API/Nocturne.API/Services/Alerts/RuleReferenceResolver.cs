using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Filters a batch of <see cref="AlertRuleSnapshot"/>s down to those whose <c>alert_state</c>
/// references all resolve to enabled rules. A rule that references a
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
/// <c>AlertReferenceService</c>; here a self-reference counts as evaluable, and the engine's
/// <c>alert_state</c> leaf reads it from the active-alert snapshot.
/// </remarks>
public static class RuleReferenceResolver
{
    /// <summary>
    /// Returns the subset of <paramref name="rules"/> that have no <c>alert_state</c> references
    /// to rule ids outside <paramref name="enabledIds"/>, which defaults to the ids of
    /// <paramref name="rules"/>. Order is preserved. A rule whose tree the engine cannot describe
    /// is kept, so the engine's per-rule skip handles it rather than this pass failing.
    /// </summary>
    public static IReadOnlyList<AlertRuleSnapshot> FilterEvaluable(
        IReadOnlyList<AlertRuleSnapshot> rules, ILogger? logger = null, IReadOnlySet<Guid>? enabledIds = null)
    {
        if (rules.Count == 0) return rules;

        enabledIds ??= rules.Select(r => r.Id).ToHashSet();

        var keep = new List<AlertRuleSnapshot>(rules.Count);
        foreach (var rule in rules)
        {
            bool resolves;
            try
            {
                resolves = AllReferencesResolve(rule, enabledIds);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Could not resolve alert_state references of alert rule {AlertRuleId}", rule.Id);
                resolves = true;
            }
            if (resolves)
                keep.Add(rule);
        }
        return keep;
    }

    private static bool AllReferencesResolve(AlertRuleSnapshot rule, IReadOnlySet<Guid> enabledIds) =>
        RuleTrees.AlertStateTargets(RuleTrees.Leaves(rule, includeAutoResolve: false))
            .All(target => target == rule.Id || enabledIds.Contains(target));
}
