using Nocturne.Core.Contracts.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The instant this process began recording <c>sustained</c> not-true instants, and the raise every
/// stored one goes through before it reaches the engine (docs/alerts/engine-semantics.md §7).
/// </summary>
/// <remarks>
/// A stored not-true instant bounds a run's start only because every false evaluation after it was
/// recorded, or covered by its lease. An image that ran before this process, an older one across a
/// rollback above all, evaluated without recording, so a stored instant from before this process
/// can be older than a false evaluation nobody recorded. Raised to this instant it is no earlier than
/// any of those, and a start after a restart can be at most the lease past it, which errs later. A row
/// is raised, never dropped: a path with no state reads as never found false, and a rule with no state
/// at all as on its first evaluation, bounded only by its <c>enabled_at</c>.
/// </remarks>
internal sealed class NotTrueRecordingFloor(TimeProvider time)
{
    /// <summary>Captured when the singleton is first resolved, before this process evaluates anything.</summary>
    public DateTime Since { get; } = time.GetUtcNow().UtcDateTime;

    /// <summary><paramref name="timers"/> with every not-true instant raised to at least <see cref="Since"/>.</summary>
    public ConditionTimers Raise(ConditionTimers timers) => timers with
    {
        NotTrue = timers.NotTrue.ToDictionary(
            kv => kv.Key,
            kv => kv.Value < Since ? Since : kv.Value,
            StringComparer.Ordinal),
    };
}
