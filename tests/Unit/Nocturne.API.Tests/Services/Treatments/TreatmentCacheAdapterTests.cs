using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// The recent-treatment cache is shared by the owner's partition, so no reader
/// <see cref="NarrowedReaders"/> lists reads or fills the owner's treatments.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCacheAdapterTests
{
    private static readonly TreatmentQuery CacheableQuery = new() { Count = 10, Skip = 0 };

    private static TreatmentCacheAdapter Adapter(ITenantCache cache) =>
        new(cache, NullLogger<TreatmentCacheAdapter>.Instance);

    [Fact]
    public async Task GetOrComputeAsync_CacheableQuery_ServesTheTenantCache()
    {
        var cached = new List<Treatment> { new() { Id = "cached" } };
        var cache = new Mock<ITenantCache>();
        cache
            .Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<List<Treatment>>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var result = await Adapter(cache.Object).GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Treatment>>([]), CancellationToken.None);

        result.Should().BeSameAs(cached);
    }

    [Fact]
    public async Task InvalidateAsync_DropsTheCachedRead()
    {
        var adapter = Adapter(TestTenantCache.Create());
        await adapter.GetOrComputeAsync(
            CacheableQuery,
            () => Task.FromResult<IReadOnlyList<Treatment>>([new Treatment { Id = "stale" }]),
            CancellationToken.None);

        await adapter.InvalidateAsync(CancellationToken.None);
        var result = await adapter.GetOrComputeAsync(
            CacheableQuery,
            () => Task.FromResult<IReadOnlyList<Treatment>>([new Treatment { Id = "fresh" }]),
            CancellationToken.None);

        result!.Single().Id.Should().Be("fresh");
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task GetOrComputeAsync_NarrowedReader_NeverSharesTheOwnersTreatments(string reader)
    {
        using var local = new MemoryCache(new MemoryCacheOptions());
        var distributed = TestTenantCache.Distributed(local);

        var (narrowedResult, ownerResult) = await ReadAfterTheOwner(
            Adapter(TestTenantCache.Create(local, distributed)),
            Adapter(NarrowedReaders.Cache(reader, local, distributed)));

        narrowedResult!.Select(t => t.Id).Should().Equal("narrowed");
        ownerResult!.Select(t => t.Id).Should().Equal("owner");
    }

    /// <summary>
    /// A share with full history is not clamped, but row level security still narrows what it
    /// reads: the legacy treatment projection spans <c>notes</c> and <c>state_spans</c>, which no
    /// share sees, and <c>bg_checks</c> and <c>device_events</c>, which only some do. Its category
    /// set is its own partition, so it neither reads the owner's rows nor hands the owner its own.
    /// </summary>
    [Fact]
    public async Task GetOrComputeAsync_FullHistoryShare_NeverSharesTheOwnersTreatments()
    {
        var share = new CategoryReadContext();
        share.MarkShare();
        share.SetVisibleCategories("glucose.read,treatments.read");
        share.SetFullHistory(true);
        share.IsHistoryClamped.Should().BeFalse("the precondition: a full-history share is not clamped");

        using var local = new MemoryCache(new MemoryCacheOptions());
        var distributed = TestTenantCache.Distributed(local);
        var (shareResult, ownerResult) = await ReadAfterTheOwner(
            Adapter(TestTenantCache.Create(local, distributed)),
            Adapter(TestTenantCache.Create(local, distributed, partition: new ReaderCachePartition(share).Current)));

        shareResult!.Select(t => t.Id).Should().Equal("narrowed");
        ownerResult!.Select(t => t.Id).Should().Equal("owner");
    }

    private static async Task<(IReadOnlyList<Treatment>? Narrowed, IReadOnlyList<Treatment>? Owner)> ReadAfterTheOwner(
        TreatmentCacheAdapter owner, TreatmentCacheAdapter narrowed)
    {
        await owner.GetOrComputeAsync(
            CacheableQuery,
            () => Task.FromResult<IReadOnlyList<Treatment>>([new Treatment { Id = "owner" }]),
            CancellationToken.None);
        var narrowedResult = await narrowed.GetOrComputeAsync(
            CacheableQuery,
            () => Task.FromResult<IReadOnlyList<Treatment>>([new Treatment { Id = "narrowed" }]),
            CancellationToken.None);
        var ownerResult = await owner.GetOrComputeAsync(
            CacheableQuery,
            () => Task.FromResult<IReadOnlyList<Treatment>>([new Treatment { Id = "recomputed" }]),
            CancellationToken.None);
        return (narrowedResult, ownerResult);
    }
}
