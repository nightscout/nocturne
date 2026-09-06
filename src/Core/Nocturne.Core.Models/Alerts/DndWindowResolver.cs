namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The Do Not Disturb picture at one instant: which scopes are in force, and the
/// tenant-wide projection that drives the <c>do_not_disturb</c> condition leaf.
/// </summary>
/// <param name="Scopes">
/// The active scopes, for <see cref="DndSuppressionGate"/>. Empty when no DND is active.
/// </param>
/// <param name="ActiveDoNotDisturb">
/// Non-null exactly when <paramref name="Scopes"/> contains <see cref="DndScope.All"/> —
/// <c>lows</c>/<c>highs</c> windows are gate-only and never trip the condition leaf.
/// </param>
/// <remarks>
/// A record class, not a <c>readonly record struct</c>: a struct's <c>default</c> would carry a
/// null <see cref="Scopes"/> and NRE on the first membership test, and there is no useful
/// "empty resolution" literal — the only way to obtain one is <see cref="DndWindowResolver.Resolve"/>.
/// </remarks>
public sealed record DndResolution(
    IReadOnlySet<DndScope> Scopes,
    DoNotDisturbSnapshot? ActiveDoNotDisturb);

/// <summary>
/// Resolves a tenant's DND windows (plus scheduled DND) into the <see cref="DndResolution"/>
/// the evaluation context carries. The single resolver for both paths: the live enricher and
/// the replay walker call this instead of each assembling the scope set and the
/// <see cref="DoNotDisturbSnapshot"/> themselves, so the two cannot drift on how a window
/// resolves (ADR 0004 D5).
/// </summary>
/// <remarks>
/// Shared window resolution is not the same as identical DND state: replay passes no
/// <c>scheduled</c> projection, because a recurring schedule's past state is not reconstructible
/// from the settings row as it stands today. Live and replay therefore still differ whenever
/// scheduled DND is on — the windows half is what this guarantees.
/// </remarks>
/// <seealso cref="DndWindowSnapshot"/>
/// <seealso cref="DndSuppressionGate"/>
public static class DndWindowResolver
{
    /// <summary>Shared empty set so the common no-DND instant allocates nothing.</summary>
    private static readonly IReadOnlySet<DndScope> NoScopes = new HashSet<DndScope>();

    /// <summary>
    /// The DND state in force at <paramref name="atUtc"/>.
    /// </summary>
    /// <param name="windows">The tenant's candidate windows (uncleared, or — for replay — receipt-bounded).</param>
    /// <param name="atUtc">The evaluation instant.</param>
    /// <param name="receiptGated">
    /// <see langword="true"/> for replay: a window only counts once the server had received it
    /// (<see cref="DndWindowSnapshot.WasActiveAt"/>), so replay never retroactively suppresses
    /// the offline-authoring gap. <see langword="false"/> for the live path.
    /// </param>
    /// <param name="scheduled">
    /// The active scheduled-DND projection, when any. Scheduled DND is tenant-wide, so it
    /// contributes <see cref="DndScope.All"/> — but an active manual all-window *outranks* it as
    /// the <c>for_minutes</c> anchor and source, so overlap never shifts the elapsed-time anchor.
    /// </param>
    public static DndResolution Resolve(
        IEnumerable<DndWindowSnapshot> windows,
        DateTime atUtc,
        bool receiptGated,
        TenantAlertSettingsSnapshot.ActiveProjection? scheduled = null)
    {
        HashSet<DndScope>? scopes = null;
        DateTime? earliestAllStartedAt = null;

        foreach (var window in windows)
        {
            var active = receiptGated ? window.WasActiveAt(atUtc) : window.IsActiveAt(atUtc);
            if (!active)
                continue;

            (scopes ??= new HashSet<DndScope>()).Add(window.Scope);

            if (window.Scope == DndScope.All
                && (earliestAllStartedAt is null || window.StartedAt < earliestAllStartedAt))
            {
                earliestAllStartedAt = window.StartedAt;
            }
        }

        if (scheduled is not null)
            (scopes ??= new HashSet<DndScope>()).Add(DndScope.All);

        var resolvedScopes = scopes is null ? NoScopes : scopes;

        // Anchor for_minutes on the earliest active all-window (a manual mute) when there is
        // one, and only fall back to the scheduled projection otherwise. Manual-wins is the
        // pre-window `TenantAlertSettingsSnapshot.Resolve` contract — it checked the manual path
        // first — and keeping it means a scheduled window opening on top of a running manual mute
        // does not restart the elapsed-time anchor under a sustained `do_not_disturb` condition.
        DoNotDisturbSnapshot? dnd = null;
        if (resolvedScopes.Contains(DndScope.All))
        {
            dnd = earliestAllStartedAt is { } startedAt
                ? new DoNotDisturbSnapshot(startedAt, "manual")
                // Unreachable unless `scheduled` put All in the set, so the ! is sound; All is
                // only ever present because one of these two branches produced it.
                : new DoNotDisturbSnapshot(scheduled!.StartedAt, scheduled.Source);
        }

        return new DndResolution(resolvedScopes, dnd);
    }
}
