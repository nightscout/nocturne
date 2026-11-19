using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Stores the tenant's Do Not Disturb windows and answers which are active. Every method runs as
/// the scope's reader on its tenant.
/// </summary>
/// <remarks>
/// A window is active exactly when <see cref="DndWindowSnapshot.IsActiveAt"/> says so, which is
/// the rule the alert engine suppresses by. Rows are kept after they expire or are cleared.
/// </remarks>
public interface IDndWindowRepository
{
    /// <summary>
    /// The windows active now, earliest started first, of <paramref name="scope"/> or of every
    /// scope when it is null.
    /// </summary>
    Task<IReadOnlyList<DndWindowRecord>> GetActiveAsync(DndScope? scope, CancellationToken ct);

    /// <summary>
    /// The windows that can still take effect at or after <paramref name="nowUtc"/>: uncleared and
    /// not past their expiry, of every scope. Every <see cref="DateTime"/> is
    /// <see cref="DateTimeKind.Utc"/>, so <see cref="DndWindowSnapshot.IsActiveAt"/> is sound.
    /// </summary>
    /// <remarks>
    /// The expiry bound keeps this bounded. A window that runs out is never cleared, so filtering
    /// on <c>cleared_at</c> alone would return every timed mute the tenant has ever set, on a path
    /// that runs once per glucose reading.
    /// </remarks>
    Task<IReadOnlyList<DndWindowSnapshot>> GetUnexpiredSnapshotsAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Every window received by <paramref name="asOfReceiptUtc"/> (<c>created_at &lt;= asOfReceiptUtc</c>),
    /// cleared and expired ones included, for replay. Replay decides each window per tick with
    /// <see cref="DndWindowSnapshot.WasActiveAt"/>, which reads <c>cleared_at</c>. Every
    /// <see cref="DateTime"/> is <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    Task<IReadOnlyList<DndWindowSnapshot>> GetReceivedByAsync(DateTime asOfReceiptUtc, CancellationToken ct);

    /// <summary>
    /// The first <paramref name="limit"/> windows in effect at some instant of <c>(from, to)</c>, earliest started
    /// first: started before <paramref name="to"/>, and neither expired nor cleared by <paramref name="from"/>.
    /// </summary>
    Task<IReadOnlyList<DndWindowRecord>> GetOverlappingAsync(DateTime from, DateTime to, int limit, CancellationToken ct);

    /// <summary>
    /// Whether any window touches <c>[from, to)</c>: started before <paramref name="to"/>, and neither expired nor
    /// cleared before <paramref name="from"/>. Reads one row at most.
    /// </summary>
    Task<bool> AnyTouchingAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="window"/> unless the tenant already holds its id, and reconciles the
    /// same-scope windows whose span overlaps it so one scope is never active twice at an instant.
    /// An earlier window is truncated to end where this one begins, and one that starts at or after
    /// this one's start is cleared with <c>system:superseded</c>. A window that has expired, or is
    /// scheduled for a span this one does not reach, is left as it is: restamping the first would
    /// falsify the audit trail, and clearing the second would destroy standing intent that adding
    /// this window said nothing about. A null expiry is an open-ended span.
    /// </summary>
    Task<DndWindowCreateResult> CreateAsync(NewDndWindow window, CancellationToken ct);

    /// <summary>
    /// Clears the window now. A window that is already cleared is returned unchanged, so a retry
    /// never rewrites the first clear. Null when the tenant has no such window.
    /// </summary>
    Task<DndWindowRecord?> ClearAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Turns the manual all-scope mute on until <paramref name="endsAt"/>. The earliest-started
    /// active all-scope window is kept and given the new expiry, so a repeat toggle does not reset
    /// its start, and any other active all-scope window is cleared with <c>system:superseded</c>.
    /// With none active it stores a new one.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CreateAsync"/> this reconciles the windows active now, not the span of a new
    /// window: the toggle has no span of its own.
    /// </remarks>
    Task EnsureManualAsync(DateTime? endsAt, CancellationToken ct);

    /// <summary>
    /// Turns the manual mute off: clears every uncleared all-scope window as a user clear, those
    /// not yet started included, so the toggle also cancels a pending mute rather than letting it
    /// activate later.
    /// </summary>
    Task ClearManualAsync(CancellationToken ct);
}
