using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Replay-only helper that evaluates every leaf in a <see cref="ConditionNode"/> tree
/// regardless of any short-circuit logic the live composite/NOT/sustained evaluators
/// would apply. Used by the replay path to record per-leaf truth at every tick so the
/// editor can render a leaf-by-leaf transition log.
/// </summary>
/// <remarks>
/// The live <c>CompositeEvaluator</c> short-circuits AND/OR — once an AND sees a false
/// child it skips the rest. That's correct for firing decisions but loses the per-leaf
/// truth needed by the replay UI. This runner walks the tree in the same pre-order DFS
/// as <see cref="LeafIdentity.AssignLeafIds"/>, evaluates each leaf in isolation against
/// the same context, and returns the booleans keyed by leaf id. Composite/Not/Sustained
/// are not looked up via the registry — the runner only descends into their children
/// (the FE recomposes their truth from the leaf log).
/// </remarks>
internal sealed class ForceEvalRunner
{
    /// <summary>
    /// Walks <paramref name="root"/> in pre-order DFS, evaluates every leaf via
    /// <paramref name="registry"/>, and returns a <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// keyed by leaf id. Leaves whose evaluator is unregistered or whose evaluation throws
    /// are recorded as <c>false</c>, mirroring the silent-fail mode the recursive
    /// evaluators already use.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, bool>> EvaluateAllLeavesAsync(
        ConditionNode root,
        SensorContext context,
        ConditionEvaluatorRegistry registry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);

        var leaves = LeafIdentity.AssignLeafIds(root);
        var result = new Dictionary<int, bool>(leaves.Count);

        foreach (var (leafId, node) in leaves)
        {
            ct.ThrowIfCancellationRequested();
            bool value;
            try
            {
                value = await registry.EvaluateNodeAsync(node, context, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                value = false;
            }
            result[leafId] = value;
        }

        return result;
    }
}
