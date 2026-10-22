namespace Nocturne.Core.Models.V4;

/// <summary>
/// The outcome of restoring many soft-deleted state spans, with each conflict named by the id the
/// caller sent.
/// </summary>
public class StateSpanBulkRestoreResult
{
    public IReadOnlyList<StateSpan> Restored { get; init; } = [];

    /// <summary>
    /// The ids, as sent, of spans left deleted because a live span holds their original id.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];
}
