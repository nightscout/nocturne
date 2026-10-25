using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>The version read for a time before a profile's first stored version.</summary>
internal enum BeforeFirstVersion
{
    /// <summary>The newest, as <see cref="ProfileVersionCache.ActiveAt{TRecord}"/> reads it.</summary>
    Newest,

    /// <summary>The oldest, as <see cref="ActiveProfileSchedulesService"/> reads it.</summary>
    Oldest,
}

internal readonly record struct VersionSlice<TRecord>(long StartMills, long EndMills, TRecord Version);

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

    /// <summary>The version in force at <paramref name="timestamp"/>; see <see cref="ActiveAt{TRecord}"/>.</summary>
    public async Task<TRecord?> GetActiveAtAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo,
        string profileName,
        DateTime timestamp,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped =>
        ActiveAt(await GetVersionsAsync(repo, profileName, ct), timestamp);

    /// <summary>Every stored version of <paramref name="profileName"/>, newest first.</summary>
    public Task<TRecord[]> GetVersionsAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo,
        string profileName,
        CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped =>
        _cache.GetOrCreateLocalAsync(Key<TRecord>(profileName), () => LoadAsync(repo, profileName, ct), Ttl);

    /// <summary>
    /// Reloads the cached versions of <paramref name="profileName"/>, for a reader that must see a
    /// version just written rather than wait out the TTL.
    /// </summary>
    public async Task RefreshAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo, string profileName, CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped =>
        _cache.SetLocal(Key<TRecord>(profileName), await LoadAsync(repo, profileName, ct), Ttl);

    private static string Key<TRecord>(string profileName) => $"profile-versions:{typeof(TRecord).Name}:{profileName}";

    private static async Task<TRecord[]> LoadAsync<TRecord>(
        IProfileScopedRepository<TRecord> repo, string profileName, CancellationToken ct)
        where TRecord : class, IV4Record, IProfileScoped =>
        (await repo.GetByProfileNameAsync(profileName, ct))
            .OrderByDescending(v => v.Timestamp)
            .ToArray();

    /// <summary>
    /// The newest of <paramref name="newestFirst"/> at-or-before <paramref name="timestamp"/>; before the
    /// first, the newest, as Nightscout reads its newest profile document (<c>data[0]</c>) for a time
    /// before every document.
    /// </summary>
    public static TRecord? ActiveAt<TRecord>(TRecord[] newestFirst, DateTime timestamp)
        where TRecord : class, IV4Record =>
        newestFirst.FirstOrDefault(v => v.Timestamp <= timestamp) ?? newestFirst.FirstOrDefault();

    /// <summary>
    /// <c>[fromMills, toMills)</c> cut at every start of <paramref name="newestFirst"/> inside it, each
    /// slice with the version in force at its start; <paramref name="beforeFirst"/> says which version a
    /// time before the first one reads. Empty when there is no version or no time.
    /// </summary>
    public static IEnumerable<VersionSlice<TRecord>> Slice<TRecord>(
        TRecord[] newestFirst, long fromMills, long toMills, BeforeFirstVersion beforeFirst = BeforeFirstVersion.Newest)
        where TRecord : class, IV4Record
    {
        if (toMills <= fromMills || newestFirst.Length == 0)
            yield break;

        TRecord InForceAt(long mills) =>
            newestFirst.FirstOrDefault(v => v.Timestamp.ToMills() <= mills)
            ?? (beforeFirst == BeforeFirstVersion.Newest ? newestFirst[0] : newestFirst[^1]);

        var start = fromMills;
        var cuts = newestFirst
            .Select(v => v.Timestamp.ToMills())
            .Where(t => t > fromMills && t < toMills)
            .Distinct()
            .Order()
            .Append(toMills);
        foreach (var cut in cuts)
        {
            yield return new VersionSlice<TRecord>(start, cut, InForceAt(start));
            start = cut;
        }
    }
}
