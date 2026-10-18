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
}
