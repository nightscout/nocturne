using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// Write-only domain service for profile data operations. Decomposes profiles directly
/// into V4 granular records via <see cref="IProfileDecomposer"/>, publishes each document it wrote
/// (<see cref="IRecordChangePublisher"/>), and notifies listeners via <see cref="IDataEventSink{T}"/>.
/// </summary>
/// <remarks>
/// A document is written as one row per table per store, each table its own commit, so the rows are
/// not announced as a document: a client rebuilding one after each commit would see a store that is
/// half the old document and half the new. The document is published once, after its last write, as
/// a read serves it, so its <c>_id</c>, stores and <c>defaultProfile</c> are the ones a client then
/// reads. A delete publishes the document as it was read before the delete.
/// </remarks>
/// <seealso cref="IProfileWriteService"/>
public class ProfileWriteService : IProfileWriteService
{
    private readonly IProfileDecomposer _decomposer;
    private readonly IProfileProjectionService _projection;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly IRecordChangePublisher _changes;
    private readonly IDataEventSink<Profile> _events;
    private readonly ILogger<ProfileWriteService> _logger;

    public ProfileWriteService(
        IProfileDecomposer decomposer,
        IProfileProjectionService projection,
        ITherapySettingsRepository therapySettings,
        IRecordChangePublisher changes,
        IDataEventSink<Profile> events,
        ILogger<ProfileWriteService> logger
    )
    {
        _decomposer = decomposer;
        _projection = projection;
        _therapySettings = therapySettings;
        _changes = changes;
        _events = events;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BulkWrite<Profile>> CreateProfilesAsync(
        IEnumerable<Profile> profiles,
        CancellationToken cancellationToken = default
    )
    {
        var profileList = profiles.ToList();

        foreach (var profile in profileList)
        {
            if (string.IsNullOrEmpty(profile.Id))
            {
                profile.Id = Guid.CreateVersion7().ToString();
            }
        }

        var named = await ReadNamedAsync(
            profileList.Where(p => p.IsValid == false).Select(p => p.Id!), cancellationToken);

        var result = await _decomposer.DecomposeBatchAsync(profileList, WriteOrigin.Live, cancellationToken);

        var written = profileList.Where(p => p.IsValid != false).ToList();
        var invalidated = profileList.Where(result.Invalidated.Contains).ToList();

        await PublishAsync(
            created: await ReadBackAsync(written),
            deleted: [.. invalidated.Select(p => named.GetValueOrDefault(p.Id!)).OfType<Profile>()]);

        if (written.Count > 0)
            await _events.OnCreatedAsync(written, cancellationToken);

        if (invalidated.Count > 0)
            await _events.OnDeletedAsync(null, cancellationToken);

        return new BulkWrite<Profile>(profileList, 0)
        {
            Invalidated = invalidated,
        };
    }

    /// <inheritdoc />
    public async Task<Profile?> UpdateProfileAsync(
        string id,
        Profile profile,
        CancellationToken cancellationToken = default
    )
    {
        profile.Id = await _projection.ResolveDocumentIdAsync(id, cancellationToken) ?? id;

        if (!await _decomposer.ReplaceDocumentAsync(profile, WriteOrigin.Live, cancellationToken))
            return null;

        await PublishAsync(updated: await ReadBackAsync([profile]));

        await _events.OnUpdatedAsync(profile, cancellationToken);

        return profile;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProfileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        var documentId = await _projection.ResolveDocumentIdAsync(id, cancellationToken) ?? id;
        var stored = await _projection.GetProfileByIdAsync(documentId, cancellationToken);
        var deleted = await _decomposer.DeleteDocumentAsync(documentId, WriteOrigin.Live, cancellationToken) > 0;

        if (deleted)
        {
            await PublishAsync(deleted: stored is null ? [] : [stored]);
            await _events.OnDeletedAsync(null, cancellationToken);
        }

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

        var deleted = new List<Profile>();
        var count = 0;
        foreach (var id in documentIds.Skip(keep))
        {
            var stored = await ReadStoredAsync(id);
            if (await _decomposer.DeleteDocumentAsync(id, WriteOrigin.Live, CancellationToken.None) > 0)
            {
                count++;
                if (stored is not null)
                    deleted.Add(stored);
            }
        }

        if (count > 0)
        {
            await PublishAsync(deleted: deleted);
            await _events.OnDeletedAsync(null, CancellationToken.None);
        }

        return count;
    }

    /// <summary>The stored documents <paramref name="ids"/> name, read before a write deletes them.</summary>
    private async Task<Dictionary<string, Profile>> ReadNamedAsync(
        IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var named = new Dictionary<string, Profile>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (await _projection.GetProfileByIdAsync(id, cancellationToken) is { } stored)
                named[id] = stored;
        }

        return named;
    }

    /// <summary>
    /// Each document as a read now serves it. One that cannot be read back is left out: a client told
    /// of it could not read what it was told.
    /// </summary>
    private async Task<IReadOnlyList<Profile>> ReadBackAsync(IReadOnlyList<Profile> profiles)
    {
        var served = new List<Profile>(profiles.Count);
        foreach (var profile in profiles)
        {
            if (await ReadStoredAsync(profile.Id!) is { } stored)
                served.Add(stored);
            else
                _logger.LogWarning("Profile {Id} was written but cannot be read back to announce it", profile.Id);
        }

        return served;
    }

    /// <summary>
    /// The stored document, read around a write that commits whatever the read does, or
    /// <see langword="null"/> when the store fails: the write is not to fail for an announcement.
    /// </summary>
    private async Task<Profile?> ReadStoredAsync(string id)
    {
        try
        {
            return await _projection.GetProfileByIdAsync(id, CancellationToken.None);
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            _logger.LogWarning(ex, "Profile {Id} could not be read to announce its write", id);
            return null;
        }
    }

    /// <summary>Publishes the documents a committed write created, updated or deleted.</summary>
    /// <remarks>Uncancellable: the write has committed, so its subscribers must still hear of it.</remarks>
    private Task PublishAsync(
        IReadOnlyList<Profile>? created = null,
        IReadOnlyList<Profile>? updated = null,
        IReadOnlyList<Profile>? deleted = null)
    {
        if ((created?.Count ?? 0) + (updated?.Count ?? 0) + (deleted?.Count ?? 0) == 0)
            return Task.CompletedTask;

        return _changes.PublishAsync(
            RecordsChanged.Of(WriteOrigin.Live, created, updated, deleted), CancellationToken.None);
    }
}
