using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing clock face configurations
/// </summary>
public interface IClockFaceService
{
    /// <summary>
    /// Get a clock face by ID (public, no auth required)
    /// </summary>
    /// <param name="id">Clock face ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Clock face if found, null otherwise</returns>
    Task<ClockFace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all clock faces for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of clock faces belonging to the user</returns>
    Task<IEnumerable<ClockFaceListItem>> GetByUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new clock face
    /// </summary>
    /// <param name="userId">User ID (owner)</param>
    /// <param name="request">Create request with name and config</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created clock face</returns>
    Task<ClockFace> CreateAsync(string userId, CreateClockFaceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing clock face
    /// </summary>
    /// <param name="id">Clock face ID</param>
    /// <param name="userId">User ID (must be owner)</param>
    /// <param name="request">Update request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated clock face if successful, null if not found or not owner</returns>
    Task<ClockFace?> UpdateAsync(Guid id, string userId, UpdateClockFaceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a clock face
    /// </summary>
    /// <param name="id">Clock face ID</param>
    /// <param name="userId">User ID (must be owner)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found or not owner</returns>
    Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default);
}
