using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Cross-rule integrity checks for the <see cref="AlertConditionType.AlertState"/> reference
/// graph: prevents deleting a rule that other rules cite, and prevents saving a rule whose
/// reference chain cycles back to itself.
/// </summary>
/// <remarks>
/// Disable-cascade (skipping evaluation of a rule whose chained parent is disabled) is
/// enforced at evaluation time by <see cref="RuleReferenceResolver"/>, not here.
/// </remarks>
public interface IAlertReferenceService
{
    /// <summary>
    /// Returns the IDs of every rule whose condition tree references <paramref name="ruleId"/>
    /// via an <c>alert_state.alertId</c> node. Used by the DELETE endpoint to refuse a
    /// destructive change with a 409 listing referencing rules.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindReferencingRulesAsync(Guid ruleId, CancellationToken ct);

    /// <summary>
    /// Follows <c>alert_state.alertId</c> edges from <paramref name="proposedReferences"/>, the
    /// rule ids the proposed condition tree names, through the other rules in the tenant. Returns
    /// true if any path leads back to <paramref name="ruleId"/>, including a direct self-reference.
    /// </summary>
    /// <remarks>
    /// Pass a null <paramref name="ruleId"/> on create, where no id has been assigned yet. Create
    /// is cycle-safe: only a direct self-reference could close a cycle, and that needs the
    /// server-generated new id.
    /// </remarks>
    Task<bool> DetectCycleAsync(Guid? ruleId, IReadOnlyCollection<Guid> proposedReferences, CancellationToken ct);
}

internal sealed class AlertReferenceService(
    IAlertRepository alerts,
    ITenantAccessor tenantAccessor,
    ILogger<AlertReferenceService> logger)
    : IAlertReferenceService
{
    public async Task<IReadOnlyList<Guid>> FindReferencingRulesAsync(Guid ruleId, CancellationToken ct)
    {
        var rules = await LoadTenantRulesAsync(ct);
        var referencing = new List<Guid>();

        foreach (var (id, references) in rules)
        {
            if (id != ruleId && references.Contains(ruleId))
                referencing.Add(id);
        }

        return referencing;
    }

    public async Task<bool> DetectCycleAsync(
        Guid? ruleId, IReadOnlyCollection<Guid> proposedReferences, CancellationToken ct)
    {
        // Without a known rule id (create) cycles cannot form: the new id is generated server-side
        // and the proposed tree cannot reference an id it does not yet know.
        if (ruleId is null) return false;

        var rules = await LoadTenantRulesAsync(ct);
        var byId = rules.ToDictionary(r => r.Id, r => r.References);

        // BFS through alert_state edges: queue every id reachable from the proposed tree.
        // We mark visited at enqueue time so a node is queued at most once even when many
        // peers reference the same id — keeps the loop linear in the rule graph size.
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var refId in proposedReferences)
        {
            if (visited.Add(refId)) queue.Enqueue(refId);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == ruleId.Value) return true;
            if (!byId.TryGetValue(current, out var nextReferences)) continue;

            foreach (var nextRef in nextReferences)
            {
                if (visited.Add(nextRef)) queue.Enqueue(nextRef);
            }
        }

        return false;
    }

    /// <summary>
    /// Every tenant rule with the rule ids its body's <c>alert_state</c> leaves name. A rule the
    /// engine cannot describe names none.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, IReadOnlyCollection<Guid> References)>> LoadTenantRulesAsync(
        CancellationToken ct)
    {
        // Cycle detection and reference scanning are exclusively called from request-scoped
        // controllers — tenant must be resolved. Failing loud here prevents a misconfigured
        // DI seam from silently doing a cross-tenant scan.
        if (!tenantAccessor.IsResolved)
        {
            throw new InvalidOperationException(
                "AlertReferenceService requires a resolved tenant context.");
        }

        var rows = await alerts.GetRuleSnapshotsAsync(tenantAccessor.TenantId, ct);

        var result = new List<(Guid, IReadOnlyCollection<Guid>)>(rows.Count);
        foreach (var r in rows)
        {
            IReadOnlyCollection<Guid> references;
            try
            {
                references = RuleTrees.AlertStateTargets(RuleTrees.Leaves(r.ConditionType, r.ConditionParams)).ToList();
            }
            catch (RustAlertEngineException ex)
            {
                logger.LogWarning(ex, "Could not read the condition tree of alert rule {AlertRuleId} for a reference walk", r.Id);
                references = [];
            }
            result.Add((r.Id, references));
        }
        return result;
    }
}
