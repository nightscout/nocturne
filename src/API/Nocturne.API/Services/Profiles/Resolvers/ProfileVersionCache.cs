using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Caches every stored version of a profile's schedule in the local tier of
/// <see cref="ITenantCache"/> and picks the version active at the asked time from the cached list,
/// so the answer never depends on which time was looked up first.
/// </summary>
public sealed class ProfileVersionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly ITenantCache _cache;

    public ProfileVersionCache(ITenantCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// The newest version at-or-before <paramref name="timestamp"/>, matching
    /// <see cref="IProfileScopedRepository{TRecord}.GetActiveAtAsync"/>.
    /// </summary>
    public async Task<TRecord?> GetActiveAtAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo,
        string profileName,
        DateTime timestamp,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
    {
        var versions = await _cache.GetOrCreateLocalAsync(
            Key<TRecord>(profileName), () => LoadAsync(repo, profileName, ct), Ttl);

        return versions.FirstOrDefault(v => v.Timestamp <= timestamp);
    }

    /// <summary>
    /// Reloads the cached versions of <paramref name="profileName"/>, for a reader that must see a
    /// version just written rather than wait out the TTL.
    /// </summary>
    public async Task RefreshAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo, string profileName, CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
        => _cache.SetLocal(Key<TRecord>(profileName), await LoadAsync(repo, profileName, ct), Ttl);

    private static string Key<TRecord>(string profileName) => $"profile-versions:{typeof(TRecord).Name}:{profileName}";

    private static async Task<TRecord[]> LoadAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo, string profileName, CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped
        => (await repo.GetByProfileNameAsync(profileName, ct))
            .OrderByDescending(v => v.Timestamp)
            .ToArray();
}
