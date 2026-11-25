namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Replay output: the sequence of truth transitions for a single leaf within a rule's
/// condition tree across the replay window. Each leaf is identified by the sequential
/// pre-order leaf id (docs/alerts/engine-semantics.md §2.2).
/// </summary>
/// <remarks>
/// Compressed at write time — only the points where the leaf's truth changed are
/// recorded. The first tick always emits a baseline point so callers can render the
/// initial state without scanning later transitions for it.
/// </remarks>
public sealed record LeafTransitionLog(int LeafId, IReadOnlyList<LeafTransitionPoint> Points);

/// <summary>
/// A single (timestamp, value) pair within a <see cref="LeafTransitionLog"/>.
/// </summary>
/// <param name="AtMs">Replay tick instant in Unix milliseconds.</param>
/// <param name="Value">Leaf truth at that tick; false when <paramref name="Unknown"/>.</param>
/// <param name="Unknown">
/// The leaf was unknown at that tick: a fact it compares was missing
/// (docs/alerts/engine-semantics.md §2.4).
/// </param>
public sealed record LeafTransitionPoint(long AtMs, bool Value, bool Unknown = false);
