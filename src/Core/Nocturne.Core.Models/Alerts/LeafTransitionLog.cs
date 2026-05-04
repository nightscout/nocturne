namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Replay output: the sequence of truth transitions for a single leaf within a rule's
/// condition tree across the replay window. Each leaf is identified by the sequential
/// id assigned by <see cref="LeafIdentity.AssignLeafIds"/>.
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
/// <param name="Value">Leaf truth at that tick.</param>
public sealed record LeafTransitionPoint(long AtMs, bool Value);
