namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// A rule's persisted <c>sustained</c> state by condition path (docs/alerts/engine-semantics.md §3).
/// A path is in <see cref="FirstTrue"/> while its child's run is unbroken, with the instant the run
/// started; in <see cref="NotTrue"/> once an evaluation found the child false, with the last such
/// evaluation, which no later run may start before; and in neither when the engine has recorded
/// nothing for it.
/// </summary>
public sealed record ConditionTimers(
    IReadOnlyDictionary<string, DateTime> FirstTrue,
    IReadOnlyDictionary<string, DateTime> NotTrue)
{
    /// <summary>No path has recorded state.</summary>
    public static ConditionTimers None { get; } =
        new(new Dictionary<string, DateTime>(), new Dictionary<string, DateTime>());
}

/// <summary>
/// Persistence port for <c>sustained</c> state, one row per <c>(ruleId, path)</c>. Rows are
/// removed when the rule's condition tree is edited so the path no longer exists, or when the
/// rule itself is deleted.
/// </summary>
public interface IConditionTimerStore
{
    /// <summary>Every path's state for <paramref name="ruleId"/>; empty maps when none.</summary>
    Task<ConditionTimers> GetAllForRuleAsync(Guid ruleId, CancellationToken ct);

    /// <summary>Records that <paramref name="path"/>'s run started at <paramref name="at"/>.</summary>
    Task SetFirstTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct);

    /// <summary>
    /// Records that <paramref name="path"/> has no run and its child was last found not true at
    /// <paramref name="at"/>.
    /// </summary>
    Task SetNotTrueAsync(Guid ruleId, string path, DateTime at, CancellationToken ct);

    /// <summary>
    /// Removes rows for <paramref name="ruleId"/> whose path is not in
    /// <paramref name="retainedPaths"/>. Used after rule edits to align the table with the current
    /// condition tree, dropping rows for paths that no longer exist while preserving those that
    /// do. An empty <paramref name="retainedPaths"/> clears every row for the rule.
    /// </summary>
    Task PruneToPathsAsync(Guid ruleId, IReadOnlyCollection<string> retainedPaths, CancellationToken ct);
}
