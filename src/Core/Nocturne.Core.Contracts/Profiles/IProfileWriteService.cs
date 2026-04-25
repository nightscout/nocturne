using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// Write-only domain service for profile data operations. Persists profiles, applies write
/// side-effects (cache invalidation, V4 decomposition), and broadcasts changes.
/// </summary>
public interface IProfileWriteService
{
    /// <summary>
    /// Create new profiles with side-effects (cache invalidation, V4 decomposition, event broadcast).
    /// </summary>
    /// <param name="profiles">Profiles to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created profiles with assigned IDs</returns>
    Task<IEnumerable<Profile>> CreateProfilesAsync(
        IEnumerable<Profile> profiles,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing profile with side-effects (cache invalidation, V4 decomposition, event broadcast).
    /// </summary>
    /// <param name="id">Profile ID to update</param>
    /// <param name="profile">Updated profile data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated profile if successful, null otherwise</returns>
    Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a profile with side-effects (cache invalidation, V4 decomposition, event broadcast).
    /// </summary>
    /// <param name="id">Profile ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default);
}
