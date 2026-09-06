namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// A Do Not Disturb window (ADR 0004) resolved to the fields the suppression gate
/// needs. "Active" is **computed, never stored** — a window is active at an instant
/// when it has started, has not been cleared, and has not passed its expiry. The
/// live gate and replay share this one resolver so they cannot drift.
/// </summary>
/// <param name="Scope">Which alerts the window silences.</param>
/// <param name="StartedAt">Intent time — when the user turned it on.</param>
/// <param name="EndsAt">Auto-expiry; null = until explicitly cleared.</param>
/// <param name="ClearedAt">When it was turned off early / superseded; null = not cleared.</param>
/// <param name="CreatedAt">Server **receipt** time — when the row was inserted.</param>
public sealed record DndWindowSnapshot(
    DndScope Scope,
    DateTime StartedAt,
    DateTime? EndsAt,
    DateTime? ClearedAt,
    DateTime CreatedAt)
{
    /// <summary>
    /// Active right now (the live gate). <paramref name="nowUtc"/> is the present, so
    /// any clear/expiry that applies is necessarily already in the past.
    /// </summary>
    public bool IsActiveAt(DateTime nowUtc) => ActiveAsOf(nowUtc, gateOnReceipt: false);

    /// <summary>
    /// Active as of a historical instant (replay). **Receipt-gated**: the window only
    /// counts once the server had received it (<see cref="CreatedAt"/> &lt;= <paramref name="atUtc"/>),
    /// so replay reproduces what the live engine actually did and never retroactively
    /// suppresses the offline-authoring gap (ADR 0004 D7). Clear/expiry are evaluated
    /// as-of the instant, not "currently".
    /// </summary>
    public bool WasActiveAt(DateTime atUtc) => ActiveAsOf(atUtc, gateOnReceipt: true);

    private bool ActiveAsOf(DateTime atUtc, bool gateOnReceipt) =>
        StartedAt <= atUtc
        && (EndsAt is not { } ends || atUtc < ends)
        && (ClearedAt is not { } cleared || atUtc < cleared)
        && (!gateOnReceipt || CreatedAt <= atUtc);
}
