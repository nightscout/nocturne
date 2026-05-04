using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;

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
    /// Walks the proposed condition tree following <c>alert_state.alertId</c> edges through
    /// other rules in the tenant. Returns true if any traversal cycles back to
    /// <paramref name="ruleId"/> (or a self-reference exists at the root).
    /// </summary>
    /// <param name="ruleId">The id of the rule being saved. Pass null on create where no
    /// id has been assigned yet — only direct self-references in <paramref name="proposedRoot"/>
    /// can introduce a cycle in that case (and they require knowing the new id, which is
    /// generated server-side, so create is cycle-safe by construction).</param>
    /// <param name="proposedRoot">The root <see cref="ConditionNode"/> being saved.</param>
    Task<bool> DetectCycleAsync(Guid? ruleId, ConditionNode proposedRoot, CancellationToken ct);
}

internal sealed class AlertReferenceService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ITenantAccessor tenantAccessor,
    ILogger<AlertReferenceService> logger)
    : IAlertReferenceService
{
    public async Task<IReadOnlyList<Guid>> FindReferencingRulesAsync(Guid ruleId, CancellationToken ct)
    {
        var rules = await LoadTenantRulesAsync(ct);
        var referencing = new List<Guid>();

        foreach (var (id, root) in rules)
        {
            if (id == ruleId) continue;
            if (root is null) continue;
            if (TreeReferences(root, ruleId))
            {
                referencing.Add(id);
            }
        }

        return referencing;
    }

    public async Task<bool> DetectCycleAsync(Guid? ruleId, ConditionNode proposedRoot, CancellationToken ct)
    {
        // Without a known rule id (create) cycles cannot form: the new id is generated server-side
        // and the proposed tree cannot reference an id it does not yet know.
        if (ruleId is null) return false;

        var rules = await LoadTenantRulesAsync(ct);
        var byId = rules.ToDictionary(r => r.Id, r => r.Root);

        // Direct self-reference in the proposed tree is the simplest cycle.
        if (TreeReferences(proposedRoot, ruleId.Value)) return true;

        // BFS through alert_state edges: queue every id reachable from the proposed tree.
        // We mark visited at enqueue time so a node is queued at most once even when many
        // peers reference the same id — keeps the loop linear in the rule graph size.
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var refId in ExtractAlertStateRefs(proposedRoot))
        {
            if (visited.Add(refId)) queue.Enqueue(refId);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == ruleId.Value) return true;
            if (!byId.TryGetValue(current, out var nextRoot) || nextRoot is null) continue;

            foreach (var nextRef in ExtractAlertStateRefs(nextRoot))
            {
                if (visited.Add(nextRef)) queue.Enqueue(nextRef);
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<(Guid Id, ConditionNode? Root)>> LoadTenantRulesAsync(CancellationToken ct)
    {
        // Cycle detection and reference scanning are exclusively called from request-scoped
        // controllers — tenant must be resolved. Failing loud here prevents a misconfigured
        // DI seam from silently doing a cross-tenant scan.
        if (!tenantAccessor.IsResolved)
        {
            throw new InvalidOperationException(
                "AlertReferenceService requires a resolved tenant context.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantAccessor.TenantId;

        var rows = await db.AlertRules
            .AsNoTracking()
            .Select(r => new { r.Id, r.ConditionType, r.ConditionParams })
            .ToListAsync(ct);

        var result = new List<(Guid, ConditionNode?)>(rows.Count);
        foreach (var r in rows)
        {
            result.Add((r.Id, TryDeserializeRoot(r.ConditionType, r.ConditionParams)));
        }
        return result;
    }

    /// <summary>
    /// Reconstructs a <see cref="ConditionNode"/> from a rule's stored <c>ConditionType</c> and
    /// <c>ConditionParams</c>. The DB stores only the type-specific payload, not the full
    /// envelope — this helper rewraps it so the walker can treat all rules uniformly.
    /// </summary>
    private ConditionNode? TryDeserializeRoot(AlertConditionType type, string conditionParams)
    {
        try
        {
            return type switch
            {
                AlertConditionType.Composite => new ConditionNode("composite",
                    Composite: JsonSerializer.Deserialize<CompositeCondition>(conditionParams, EvaluatorJson.Options)),
                AlertConditionType.Not => new ConditionNode("not",
                    Not: JsonSerializer.Deserialize<NotCondition>(conditionParams, EvaluatorJson.Options)),
                AlertConditionType.Sustained => new ConditionNode("sustained",
                    Sustained: JsonSerializer.Deserialize<SustainedCondition>(conditionParams, EvaluatorJson.Options)),
                AlertConditionType.AlertState => new ConditionNode("alert_state",
                    AlertState: JsonSerializer.Deserialize<AlertStateCondition>(conditionParams, EvaluatorJson.Options)),
                // Leaf kinds with no children — they cannot host alert_state references, so
                // the wrapper shape doesn't matter for the walker; return any matching node.
                _ => new ConditionNode(type.ToString().ToLowerInvariant()),
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize ConditionParams for reference walk");
            return null;
        }
    }

    private static bool TreeReferences(ConditionNode node, Guid targetId)
    {
        foreach (var refId in ExtractAlertStateRefs(node))
        {
            if (refId == targetId) return true;
        }
        return false;
    }

    private static IEnumerable<Guid> ExtractAlertStateRefs(ConditionNode node)
    {
        if (node.AlertState is { } alertState) yield return alertState.AlertId;
        if (node.Composite is { } composite)
        {
            foreach (var child in composite.Conditions)
                foreach (var id in ExtractAlertStateRefs(child)) yield return id;
        }
        if (node.Not is { Child: { } notChild })
        {
            foreach (var id in ExtractAlertStateRefs(notChild)) yield return id;
        }
        if (node.Sustained is { Child: { } sustainedChild })
        {
            foreach (var id in ExtractAlertStateRefs(sustainedChild)) yield return id;
        }
    }
}
