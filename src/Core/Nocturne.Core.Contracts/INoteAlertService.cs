namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for evaluating and processing note alerts based on tracker thresholds
/// </summary>
public interface INoteAlertService
{
    /// <summary>
    /// Get pending note alerts for a user based on note tracker links and threshold evaluations
    /// </summary>
    /// <param name="userId">User ID to get pending alerts for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of pending note alerts</returns>
    Task<IEnumerable<NoteAlert>> GetPendingNoteAlertsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process and dispatch note alerts for all users (called by background service)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ProcessNoteAlertsAsync(CancellationToken cancellationToken = default);
}
