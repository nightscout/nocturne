using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
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
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<Profile> _events;
    private readonly ILogger<ProfileWriteService> _logger;
    private const string CollectionName = "profiles";

    public ProfileWriteService(
        IProfileDecomposer decomposer,
        ITherapySettingsRepository therapySettings,
        IWriteSideEffects sideEffects,
        IDataEventSink<Profile> events,
        ILogger<ProfileWriteService> logger
    )
    {
        _decomposer = decomposer;
        _therapySettings = therapySettings;
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
        }

        await _decomposer.DecomposeBatchAsync(profileList, WriteOrigin.Live, cancellationToken);

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
        profile.Id = id;

        if (!await _decomposer.ReplaceDocumentAsync(profile, WriteOrigin.Live, cancellationToken))
            return null;

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
        var deleted = await _decomposer.DeleteDocumentAsync(id, WriteOrigin.Live, cancellationToken) > 0;

        if (deleted)
            await OnDeletedAsync(cancellationToken);

        return deleted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Once the documents to delete are chosen the deletes run to the end, so a request dropped
    /// part way leaves no half-pruned collection behind.
    /// </remarks>
    public async Task<int> PruneProfilesAsync(
        int keep,
        CancellationToken cancellationToken = default
    )
    {
        var documentIds = await _therapySettings.GetDocumentIdsAsync(cancellationToken);

        var deleted = 0;
        foreach (var id in documentIds.Skip(keep))
        {
            if (await _decomposer.DeleteDocumentAsync(id, WriteOrigin.Live, CancellationToken.None) > 0)
                deleted++;
        }

        if (deleted > 0)
            await OnDeletedAsync(CancellationToken.None);

        return deleted;
    }

    private async Task OnDeletedAsync(CancellationToken cancellationToken)
    {
        await _sideEffects.OnDeletedAsync<Profile>(
            CollectionName,
            null,
            cancellationToken: cancellationToken
        );

        await _events.OnDeletedAsync(null, cancellationToken);
    }
}
