using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Write-only domain service for profile data operations. Persists profiles via
/// <see cref="IProfileRepository"/>, applies write side-effects via <see cref="IWriteSideEffects"/>
/// (cache invalidation, V4 decomposition), and broadcasts changes via
/// <see cref="IDataEventSink{T}"/>.
/// </summary>
/// <seealso cref="IProfileWriteService"/>
public class ProfileWriteService : IProfileWriteService
{
    private readonly IProfileRepository _profiles;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<Profile> _events;
    private readonly ILogger<ProfileWriteService> _logger;
    private const string CollectionName = "profiles";

    public ProfileWriteService(
        IProfileRepository profiles,
        IWriteSideEffects sideEffects,
        IDataEventSink<Profile> events,
        ILogger<ProfileWriteService> logger
    )
    {
        _profiles = profiles;
        _sideEffects = sideEffects;
        _events = events;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Profile>> CreateProfilesAsync(
        IEnumerable<Profile> profiles,
        CancellationToken cancellationToken = default
    )
    {
        var createdProfiles = await _profiles.CreateProfilesAsync(
            profiles,
            cancellationToken
        );

        await _sideEffects.OnCreatedAsync(
            CollectionName,
            createdProfiles.ToList(),
            cancellationToken: cancellationToken
        );

        await _events.OnCreatedAsync(createdProfiles.ToList(), cancellationToken);

        return createdProfiles;
    }

    /// <inheritdoc />
    public async Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    )
    {
        var updatedProfile = await _profiles.UpdateProfileAsync(
            id,
            profile,
            cancellationToken
        );

        if (updatedProfile != null)
        {
            await _sideEffects.OnUpdatedAsync(
                CollectionName,
                updatedProfile,
                cancellationToken: cancellationToken
            );

            await _events.OnUpdatedAsync(updatedProfile, cancellationToken);
        }

        return updatedProfile;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProfileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var profileToDelete = await _profiles.GetProfileByIdAsync(id, cancellationToken);

        var deleted = await _profiles.DeleteProfileAsync(id, cancellationToken);

        if (deleted)
        {
            await _sideEffects.OnDeletedAsync(
                CollectionName,
                profileToDelete,
                cancellationToken: cancellationToken
            );

            await _events.OnDeletedAsync(profileToDelete, cancellationToken);
        }

        return deleted;
    }
}
