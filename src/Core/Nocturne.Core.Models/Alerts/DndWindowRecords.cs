namespace Nocturne.Core.Models.Alerts;

/// <summary>A stored Do Not Disturb window as read.</summary>
/// <param name="Id">The client-supplied id.</param>
/// <param name="Scope">Which alerts the window silences.</param>
/// <param name="StartedAt">Intent time, which may predate <paramref name="CreatedAt"/>.</param>
/// <param name="EndsAt">Auto-expiry; null mutes until cleared.</param>
/// <param name="ClearedAt">When it was turned off or superseded; null if it was not.</param>
/// <param name="ClearedBy">Who cleared it; null for a user clear.</param>
/// <param name="Source">What authored the window.</param>
/// <param name="CreatedAt">Server receipt time.</param>
public sealed record DndWindowRecord(
    Guid Id,
    DndScope Scope,
    DateTime StartedAt,
    DateTime? EndsAt,
    DateTime? ClearedAt,
    string? ClearedBy,
    string? Source,
    DateTime CreatedAt);

/// <summary>A window to store. Its receipt time is the server's.</summary>
public sealed record NewDndWindow(
    Guid Id,
    DndScope Scope,
    DateTime StartedAt,
    DateTime? EndsAt,
    string? Source);

/// <summary>What storing a <see cref="NewDndWindow"/> did.</summary>
public enum DndWindowCreateOutcome
{
    /// <summary>The window was stored.</summary>
    Created,

    /// <summary>The tenant already held a window with the id, which is returned unchanged.</summary>
    Existing,

    /// <summary>The id belongs to another tenant, so no window was stored.</summary>
    IdInUse,
}

/// <summary>The result of storing a window; <see cref="Window"/> is null when the id is in use.</summary>
public sealed record DndWindowCreateResult(DndWindowCreateOutcome Outcome, DndWindowRecord? Window);

/// <summary>
/// The manual all-scope Do Not Disturb toggle: on until <paramref name="EndsAt"/> (null is
/// indefinite), or off.
/// </summary>
public sealed record ManualDndToggle(bool On, DateTime? EndsAt);
