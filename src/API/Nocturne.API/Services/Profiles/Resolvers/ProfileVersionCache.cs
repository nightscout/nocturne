using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// Caches every stored version of a profile's schedule in the local tier of
/// <see cref="ITenantCache"/> and picks the version active at the asked time from the cached list,
/// so the answer never depends on which time was looked up first.
/// </summary>
internal sealed class ProfileVersionCache
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
            $"profile-versions:{typeof(TRecord).Name}:{profileName}",
            async () => (await repo.GetByProfileNameAsync(profileName, ct))
                .OrderByDescending(v => v.Timestamp)
                .ToArray(),
            Ttl);

        return versions.FirstOrDefault(v => v.Timestamp <= timestamp);
    }
}
