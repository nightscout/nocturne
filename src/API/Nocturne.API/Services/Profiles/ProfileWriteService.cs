using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Write-only domain service for profile data operations. Decomposes profiles directly
/// into V4 granular records via <see cref="IProfileDecomposer"/>, applies cache
/// invalidation and broadcasting via <see cref="IWriteSideEffects"/>, and notifies
/// listeners via <see cref="IDataEventSink{T}"/>.
/// </summary>
/// <seealso cref="IProfileWriteService"/>
public class ProfileWriteService : IProfileWriteService
{
    private readonly IProfileDecomposer _decomposer;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<Profile> _events;
    private readonly ILogger<ProfileWriteService> _logger;
    private const string CollectionName = "profiles";

    public ProfileWriteService(
        IProfileDecomposer decomposer,
        IWriteSideEffects sideEffects,
        IDataEventSink<Profile> events,
        ILogger<ProfileWriteService> logger
    )
    {
        _decomposer = decomposer;
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
        var profileList = profiles.ToList();

        foreach (var profile in profileList)
        {
            // Assign an ID if not already set
            if (string.IsNullOrEmpty(profile.Id))
            {
                profile.Id = Guid.CreateVersion7().ToString();
            }

            await _decomposer.DecomposeAsync(profile, cancellationToken);
        }

        await _sideEffects.OnCreatedAsync(
            CollectionName,
            profileList,
            cancellationToken: cancellationToken
        );

        await _events.OnCreatedAsync(profileList, cancellationToken);

        return profileList;
    }

    /// <inheritdoc />
    public async Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    )
    {
        // Ensure the profile has the correct ID for decomposition
        profile.Id = id;

        await _decomposer.DecomposeAsync(profile, cancellationToken);

        await _sideEffects.OnUpdatedAsync(
            CollectionName,
            profile,
            cancellationToken: cancellationToken
        );

        await _events.OnUpdatedAsync(profile, cancellationToken);

        return profile;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProfileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var deleted = await _decomposer.DeleteByLegacyIdAsync(id, cancellationToken);

        if (deleted > 0)
        {
            await _sideEffects.OnDeletedAsync<Profile>(
                CollectionName,
                null,
                cancellationToken: cancellationToken
            );

            await _events.OnDeletedAsync(null, cancellationToken);
        }

        return deleted > 0;
    }
}
