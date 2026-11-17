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
    /// <returns>Created profiles with assigned IDs</returns>
    Task<IEnumerable<Profile>> CreateProfilesAsync(
        IEnumerable<Profile> profiles,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Replace the profile document <paramref name="id"/> resolves to through
    /// <see cref="IProfileProjectionService.ResolveDocumentIdAsync"/> with <paramref name="profile"/>,
    /// inserting it under <paramref name="id"/> when none is stored, through
    /// <see cref="V4.IProfileDecomposer.ReplaceDocumentAsync"/>.
    /// </summary>
    /// <param name="profile">Updated profile data</param>
    /// <returns>The saved profile, or null when a user had deleted that document and it was not rewritten</returns>
    Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a profile with side-effects (cache invalidation, V4 decomposition, event broadcast),
    /// resolving <paramref name="id"/> through <see cref="IProfileProjectionService.ResolveDocumentIdAsync"/>
    /// and deleting through <see cref="V4.IProfileDecomposer.DeleteDocumentAsync"/>.
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteProfileAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every profile document except the newest <paramref name="keep"/>, ranked by
    /// <see cref="V4.Repositories.ITherapySettingsRepository.GetDocumentIdsAsync"/>.
    /// </summary>
    /// <returns>The number of profile documents deleted.</returns>
    Task<int> PruneProfilesAsync(int keep, CancellationToken cancellationToken = default);
}
