using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Entries;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Entries;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// The recent-entry cache is shared by the owner's partition, so no reader
/// <see cref="NarrowedReaders"/> lists reads or fills the owner's entries.
/// </summary>
[Trait("Category", "Unit")]
public class EntryCacheAdapterTests
{
    private static readonly EntryQuery CacheableQuery = new() { Count = 10, Skip = 0 };

    private static EntryCacheAdapter Adapter(ITenantCache cache) =>
        new(cache, NullLogger<EntryCacheAdapter>.Instance);

    [Fact]
    public async Task GetOrComputeAsync_CacheableQuery_ServesTheTenantCache()
    {
        var cached = new List<Entry> { new() { Id = "cached" } };
        var cache = new Mock<ITenantCache>();
        cache
            .Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<List<Entry>>>>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var result = await Adapter(cache.Object).GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([]));

        result.Should().BeSameAs(cached);
    }

    [Fact]
    public async Task GetOrComputeAsync_SkippingQuery_LeavesTheCacheAlone()
    {
        var cache = new Mock<ITenantCache>();

        var result = await Adapter(cache.Object).GetOrComputeAsync(
            new EntryQuery { Count = 10, Skip = 5 }, () => Task.FromResult<IReadOnlyList<Entry>>([]));

        result.Should().BeNull("a null answer sends the caller straight to the store");
        cache.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidateAsync_ClearsTheCurrentEntry_InEitherDemoSegment(bool demo)
    {
        var adapter = Adapter(TestTenantCache.Create(demo: demo));
        await adapter.GetOrComputeCurrentAsync(() => Task.FromResult<Entry?>(new Entry { Id = "stale" }));

        await adapter.InvalidateAsync();
        var result = await adapter.GetOrComputeCurrentAsync(() => Task.FromResult<Entry?>(new Entry { Id = "fresh" }));

        result!.Id.Should().Be("fresh");
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task GetOrComputeAsync_NarrowedReader_NeverSharesTheOwnersEntries(string reader)
    {
        using var local = new MemoryCache(new MemoryCacheOptions());
        var distributed = TestTenantCache.Distributed(local);
        var owner = Adapter(TestTenantCache.Create(local, distributed));
        var narrowed = Adapter(NarrowedReaders.Cache(reader, local, distributed));

        await owner.GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([new Entry { Id = "owner" }]));
        var narrowedResult = await narrowed.GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([new Entry { Id = "narrowed" }]));
        var ownerResult = await owner.GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([new Entry { Id = "recomputed" }]));

        narrowedResult!.Select(e => e.Id).Should().Equal("narrowed");
        ownerResult!.Select(e => e.Id).Should().Equal("owner");
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task GetOrComputeCurrentAsync_NarrowedReader_NeverSharesTheOwnersEntry(string reader)
    {
        using var local = new MemoryCache(new MemoryCacheOptions());
        var distributed = TestTenantCache.Distributed(local);
        var owner = Adapter(TestTenantCache.Create(local, distributed));
        var narrowed = Adapter(NarrowedReaders.Cache(reader, local, distributed));

        await owner.GetOrComputeCurrentAsync(() => Task.FromResult<Entry?>(new Entry { Id = "owner" }));
        var narrowedResult = await narrowed.GetOrComputeCurrentAsync(
            () => Task.FromResult<Entry?>(new Entry { Id = "narrowed" }));
        var ownerResult = await owner.GetOrComputeCurrentAsync(
            () => Task.FromResult<Entry?>(new Entry { Id = "recomputed" }));

        narrowedResult!.Id.Should().Be("narrowed");
        ownerResult!.Id.Should().Be("owner");
    }
}
