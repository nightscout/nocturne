namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Persistence port for sustained-condition timers. The sustained evaluator records the
/// first instant a child condition transitioned to true at <c>(ruleId, path)</c>; the elapsed
/// duration since that instant is what the sustained window is measured against. Rows are
/// removed when the child becomes false again, when the rule's condition tree is edited so
/// the path no longer exists, or when the rule itself is deleted.
/// </summary>
public interface IConditionTimerStore
{
    /// <summary>
    /// Returns every active timer for <paramref name="ruleId"/> as a
    /// <c>path → first-true instant</c> map (empty when none).
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetAllForRuleAsync(Guid ruleId, CancellationToken ct);

    /// <summary>
    /// Records (or replaces) the first-true instant for <paramref name="path"/> on
    /// <paramref name="ruleId"/>. Acts as an upsert keyed by <c>(ruleId, path)</c>.
    /// </summary>
    Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct);

    /// <summary>
    /// Removes the timer for a single <c>(ruleId, path)</c>. Used when the child condition
    /// transitions back to false and the sustained window must restart on the next true.
    /// </summary>
    Task ClearAsync(Guid ruleId, string path, CancellationToken ct);

    /// <summary>
    /// Removes timers for <paramref name="ruleId"/> whose path is not in
    /// <paramref name="retainedPaths"/>. Used after rule edits to align the timer table
    /// with the current condition tree, dropping orphan timers for paths that no longer
    /// exist while preserving those that do. An empty <paramref name="retainedPaths"/>
    /// clears every timer for the rule.
    /// </summary>
    Task PruneToPathsAsync(Guid ruleId, IReadOnlyCollection<string> retainedPaths, CancellationToken ct);
}
