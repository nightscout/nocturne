using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Platform;

/// <summary>
/// Service for managing clock face configurations
/// </summary>
public interface IClockFaceService
{
    /// <summary>
    /// Get a clock face by ID (public, no auth required)
    /// </summary>
    /// <returns>Clock face if found, null otherwise</returns>
    Task<ClockFace?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <returns>List of clock faces belonging to the user</returns>
    Task<IEnumerable<ClockFaceListItem>> GetByUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <see cref="ClockFaceConfig.Starter"/> layout with the user's current display preferences
    /// </summary>
    /// <param name="userId">User ID (the creator)</param>
    Task<ClockFaceConfig> GetStarterConfigAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new clock face, starting from <see cref="GetStarterConfigAsync"/> when the request carries no config
    /// </summary>
    /// <param name="userId">User ID (owner)</param>
    /// <param name="request">Create request with name and config</param>
    Task<ClockFace> CreateAsync(string userId, CreateClockFaceRequest request, CancellationToken cancellationToken = default);

    /// <param name="userId">User ID (must be owner)</param>
    /// <returns>Updated clock face if successful, null if not found or not owner</returns>
    Task<ClockFace?> UpdateAsync(Guid id, string userId, UpdateClockFaceRequest request, CancellationToken cancellationToken = default);

    /// <param name="userId">User ID (must be owner)</param>
    /// <returns>True if deleted, false if not found or not owner</returns>
    Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default);
}
