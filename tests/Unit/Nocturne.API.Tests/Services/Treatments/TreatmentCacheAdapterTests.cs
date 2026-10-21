using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Platform;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// The recent-treatment cache is keyed by tenant alone, so every caller
/// <see cref="CacheBypassingCallers"/> lists stays out of it entirely.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCacheAdapterTests
{
    private static readonly TreatmentQuery CacheableQuery = new() { Count = 10, Skip = 0 };

    private readonly Mock<ICacheService> _cache = new();
    private readonly CategoryReadContext _categoryReadContext = new();
    private readonly List<Treatment> _cachedTreatments = [new() { Id = "cached" }];

    public TreatmentCacheAdapterTests()
    {
        _cache
            .Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<List<Treatment>>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_cachedTreatments);
    }

    private TreatmentCacheAdapter Adapter()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.Context)
            .Returns(new TenantContext(Guid.CreateVersion7(), "test", "Test", true, false));

        return new TreatmentCacheAdapter(
            _cache.Object,
            Mock.Of<IDemoModeService>(),
            tenant.Object,
            _categoryReadContext,
            NullLogger<TreatmentCacheAdapter>.Instance);
    }

    [Fact]
    public async Task GetOrComputeAsync_Unclamped_ServesTheTenantCache()
    {
        var result = await Adapter().GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Treatment>>([]), CancellationToken.None);

        result.Should().BeSameAs(_cachedTreatments);
    }

    [Theory]
    [MemberData(nameof(CacheBypassingCallers.All), MemberType = typeof(CacheBypassingCallers))]
    public async Task GetOrComputeAsync_BypassingCaller_NeitherReadsNorWritesTheCache(string caller)
    {
        CacheBypassingCallers.Become(_categoryReadContext, caller);

        var result = await Adapter().GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Treatment>>([]), CancellationToken.None);

        result.Should().BeNull("a null answer sends the caller straight to the store");
        _cache.Invocations.Should().BeEmpty();
    }

    /// <summary>
    /// A share with full history is not clamped, but row level security still narrows what it
    /// reads: the legacy treatment projection spans <c>notes</c> and <c>state_spans</c>, which no
    /// share sees, and <c>bg_checks</c> and <c>device_events</c>, which only some do. The key
    /// carries the tenant alone, so sharing it would hand the share the owner's rows and the owner
    /// the share's. The multi-period statistics cache stays out for the same reason.
    /// </summary>
    [Fact]
    public async Task GetOrComputeAsync_FullHistoryShare_NeitherReadsNorWritesTheCache()
    {
        _categoryReadContext.MarkShare();
        _categoryReadContext.SetVisibleCategories("glucose.read,treatments.read");
        _categoryReadContext.SetFullHistory(true);
        _categoryReadContext.IsHistoryClamped.Should().BeFalse("the precondition: a full-history share is not clamped");

        var result = await Adapter().GetOrComputeAsync(
            CacheableQuery, () => Task.FromResult<IReadOnlyList<Treatment>>([]), CancellationToken.None);

        result.Should().BeNull("a null answer sends the caller straight to the store");
        _cache.Invocations.Should().BeEmpty();
    }
}
