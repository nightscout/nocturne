using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// The recent-entry cache is keyed by tenant alone, so a history-clamped reader has to stay out of
/// it entirely: a hit would serve it entries an unclamped reader loaded, and a write would hand
/// its narrowed entries to the next unclamped reader.
/// </summary>
[Trait("Category", "Unit")]
public class EntryCacheAdapterTests
{
    private static readonly EntryQuery CacheableQuery = new() { Count = 10, Skip = 0 };

    private readonly Mock<ICacheService> _cache = new();
    private readonly CategoryReadContext _categoryReadContext = new();
    private readonly List<Entry> _cachedEntries = [new() { Id = "cached" }];
    private readonly Entry _cachedCurrent = new() { Id = "cached-current" };

    public EntryCacheAdapterTests()
    {
        _cache
            .Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<List<Entry>>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_cachedEntries);
        _cache
            .Setup(c => c.GetAsync<Entry>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_cachedCurrent);
    }

    private EntryCacheAdapter Adapter()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.Context)
            .Returns(new TenantContext(Guid.CreateVersion7(), "test", "Test", true, false));

        return new EntryCacheAdapter(
            _cache.Object,
            Mock.Of<IDemoModeService>(),
            tenant.Object,
            _categoryReadContext,
            NullLogger<EntryCacheAdapter>.Instance);
    }

    private void Clamp(bool share)
    {
        if (share)
            _categoryReadContext.MarkShare();
        else
            _categoryReadContext.ClampMemberHistory();
    }

    [Fact]
    public async Task GetOrComputeAsync_Unclamped_ServesTheTenantCache()
    {
        var result = await Adapter().GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([]));

        result.Should().BeSameAs(_cachedEntries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetOrComputeAsync_HistoryClamped_NeitherReadsNorWritesTheCache(bool share)
    {
        Clamp(share);

        var result = await Adapter().GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Entry>>([]));

        result.Should().BeNull("a null answer sends the caller straight to the store");
        _cache.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrComputeCurrentAsync_Unclamped_ServesTheTenantCache()
    {
        var result = await Adapter().GetOrComputeCurrentAsync(() => Task.FromResult<Entry?>(null));

        result.Should().BeSameAs(_cachedCurrent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetOrComputeCurrentAsync_HistoryClamped_ComputesWithoutTouchingTheCache(bool share)
    {
        Clamp(share);
        var computed = new Entry { Id = "computed" };

        var result = await Adapter().GetOrComputeCurrentAsync(() => Task.FromResult<Entry?>(computed));

        result.Should().BeSameAs(computed);
        _cache.Invocations.Should().BeEmpty();
    }
}
