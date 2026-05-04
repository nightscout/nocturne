namespace Nocturne.Core.Contracts.CoachMarks;

using Nocturne.Core.Models.CoachMarks;

/// <summary>
/// Domain service for managing per-user coach mark progression.
/// </summary>
public interface ICoachMarkService
{
    /// <summary>
    /// Get all coach mark states for the current user.
    /// </summary>
    Task<IReadOnlyList<CoachMarkState>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or update a coach mark's status, setting timestamp fields as appropriate.
    /// </summary>
    Task<CoachMarkState> UpsertAsync(string markKey, string status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all coach mark states for the current user, resetting tutorials.
    /// </summary>
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}
