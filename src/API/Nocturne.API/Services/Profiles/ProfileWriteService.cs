using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
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
    private readonly IProfileProjectionService _projection;
    private readonly ITherapySettingsRepository _therapySettings;
    private readonly IWriteSideEffects _sideEffects;
    private readonly IDataEventSink<Profile> _events;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<ProfileWriteService> _logger;
    private const string CollectionName = "profiles";

    public ProfileWriteService(
        IProfileDecomposer decomposer,
        IProfileProjectionService projection,
        ITherapySettingsRepository therapySettings,
        IWriteSideEffects sideEffects,
        IDataEventSink<Profile> events,
        IAuditContext auditContext,
        ILogger<ProfileWriteService> logger
    )
    {
        _decomposer = decomposer;
        _projection = projection;
        _therapySettings = therapySettings;
        _sideEffects = sideEffects;
        _events = events;
        _auditContext = auditContext;
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
            // Assign an ID if not already set
            if (string.IsNullOrEmpty(profile.Id))
            {
                profile.Id = Guid.CreateVersion7().ToString();
            }
        }

        if (!profileList.Any(p => p.IsValid == false))
        {
            await WriteAsync(profileList, cancellationToken);
            return [.. profileList];
        }

        var sorted = await SortInvalidAsync(profileList, cancellationToken);
        var invalidated = new List<Profile>();
        using (SystemAuditScope.Push(_auditContext, keepWriter: true))
        {
            foreach (var (profile, _) in sorted.Invalidating.DistinctBy(p => p.Profile.Id, StringComparer.Ordinal))
            {
                if (await _decomposer.DeleteDocumentAsync(profile.Id!, WriteOrigin.Live, cancellationToken) > 0)
                    invalidated.Add(profile);
            }
        }

        if (sorted.StoredDeleted.Count > 0)
            await _decomposer.StoreDeletedAsync([.. sorted.StoredDeleted.Select(p => p.Profile)], cancellationToken);
        if (sorted.Writing.Count > 0)
            await WriteAsync([.. sorted.Writing.Select(p => p.Profile)], cancellationToken);
        if (invalidated.Count > 0)
            await OnDeletedAsync(cancellationToken);

        return new BulkWrite<Profile>(profileList, 0) { Invalidated = invalidated };
    }

    private async Task WriteAsync(List<Profile> profiles, CancellationToken cancellationToken)
    {
        await _decomposer.DecomposeBatchAsync(profiles, WriteOrigin.Live, cancellationToken);

        await _sideEffects.OnCreatedAsync(
            CollectionName,
            profiles,
            cancellationToken: cancellationToken
        );

        await _events.OnCreatedAsync(profiles, cancellationToken);
    }

    /// <summary>
    /// Sorts <paramref name="profiles"/> by <see cref="InvalidUploads{TUpload}"/>: a document sent with
    /// <c>isValid: false</c> whose id holds live stores deletes them, and one whose id holds none is
    /// stored deleted. A document is named only by its id, resolved first as PUT and DELETE resolve it,
    /// and a deleted one is found by the stores it carries.
    /// </summary>
    private async Task<InvalidUploads<(Profile Profile, int Index)>> SortInvalidAsync(
        List<Profile> profiles, CancellationToken cancellationToken)
    {
        foreach (var versions in profiles.GroupBy(p => p.Id!, StringComparer.Ordinal).Where(v => v.Last().IsValid == false))
        {
            if (await _projection.ResolveDocumentIdAsync(versions.Key, cancellationToken) is { } documentId)
            {
                foreach (var version in versions)
                    version.Id = documentId;
            }
        }

        var ids = profiles.Where(p => p.IsValid == false).Select(p => p.Id!).Distinct(StringComparer.Ordinal).ToList();
        var live = await _therapySettings.GetDocumentRowsAsync(ids, cancellationToken);
        var indexed = profiles.Select((profile, index) => (Profile: profile, Index: index)).ToList();

        return await InvalidUploads<(Profile Profile, int Index)>.SortAsync(indexed, indexed, new(
            p => p.Profile.Id,
            p => p.Index,
            p => p.Profile.IsValid,
            (p, valid) => p.Profile.IsValid = valid,
            p => live.ContainsKey(p.Profile.Id!),
            _ => true,
            async (unstored, token) =>
            {
                var stores = profiles
                    .Where(p => unstored.Contains(p.Id!))
                    .SelectMany(p => p.Store.Keys.Select(store => (LegacyId: $"{p.Id}:{store}", DocumentId: p.Id!)))
                    .DistinctBy(s => s.LegacyId, StringComparer.Ordinal)
                    .ToDictionary(s => s.LegacyId, s => s.DocumentId, StringComparer.Ordinal);
                var deleted = await _therapySettings.GetDeletedLegacyIdsAsync(stores.Keys, token);
                return deleted.Select(legacyId => stores[legacyId]).ToHashSet(StringComparer.Ordinal);
            }), cancellationToken);
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
        var documentId = await _projection.ResolveDocumentIdAsync(id, cancellationToken) ?? id;
        var deleted = await _decomposer.DeleteDocumentAsync(documentId, WriteOrigin.Live, cancellationToken) > 0;

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
