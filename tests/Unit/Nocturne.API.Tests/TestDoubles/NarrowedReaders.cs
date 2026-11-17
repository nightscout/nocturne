using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Caching;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Readers who see different rows from the owner, so <see cref="ReaderCachePartition"/> never puts
/// them in the owner's partition, for theories that pin each cache site to it.
/// </summary>
internal static class NarrowedReaders
{
    public const string ClampedMember = "clamped member";
    public const string Share = "share";
    public const string FullHistoryShare = "full-history share";

    public static TheoryData<string> All => [ClampedMember, Share, FullHistoryShare];

    public static CategoryReadContext Context(string reader)
    {
        var context = new CategoryReadContext();
        switch (reader)
        {
            case ClampedMember:
                context.ClampMemberHistory();
                break;
            case Share:
                context.MarkShare();
                break;
            case FullHistoryShare:
                context.MarkShare();
                context.SetFullHistory(true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reader), reader, null);
        }

        return context;
    }

    /// <summary>The <see cref="ITenantCache"/> <paramref name="reader"/> gets over the owner's tiers.</summary>
    public static ITenantCache Cache(string reader, IMemoryCache local, ICacheService? distributed = null) =>
        TestTenantCache.Create(local, distributed, partition: new ReaderCachePartition(Context(reader)).Current);
}
