using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>The per-window assignment cache of <see cref="BasalSegmentService"/>.</summary>
public sealed class BasalSegmentServiceCacheTests : IDisposable
{
    private const long From = 1705320000000;
    private const long To = From + 3_600_000;

    private readonly Mock<IActiveProfileResolver> _activeProfile = ActiveProfileResolverMock.WithDefault();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public BasalSegmentServiceCacheTests() =>
        _activeProfile
            .Setup(a => a.GetProfileSpansForRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

    public void Dispose() => _cache.Dispose();

    private BasalSegmentService Service(ITenantCache cache) => new(
        _activeProfile.Object,
        Mock.Of<IBasalScheduleRepository>(),
        Mock.Of<ITherapySettingsRepository>(),
        Mock.Of<IPatientRecordRepository>(),
        cache);

    private BasalSegmentService Service() => Service(TestTenantCache.Create(_cache));

    private static async Task DrainAsync(BasalSegmentService service)
    {
        await foreach (var _ in service.GetSegmentsAsync(From, To)) { }
    }

    private void VerifySpanReads(int times) =>
        _activeProfile.Verify(
            a => a.GetProfileSpansForRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    [Fact]
    public async Task A_repeated_window_is_served_from_the_cache()
    {
        var service = Service();

        await DrainAsync(service);
        await DrainAsync(service);

        VerifySpanReads(1);
    }

    [Theory]
    [MemberData(nameof(NarrowedReaders.All), MemberType = typeof(NarrowedReaders))]
    public async Task A_narrowed_reader_never_reads_the_owners_cached_window(string reader)
    {
        var owner = Service();
        var narrowed = Service(NarrowedReaders.Cache(reader, _cache));

        await DrainAsync(owner);
        await DrainAsync(narrowed);
        await DrainAsync(owner);

        VerifySpanReads(2);
    }
}
