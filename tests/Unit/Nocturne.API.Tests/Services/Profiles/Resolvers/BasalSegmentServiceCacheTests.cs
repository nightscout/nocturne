using Microsoft.Extensions.Caching.Memory;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>The per-window assignment cache of <see cref="BasalSegmentService"/>.</summary>
public sealed class BasalSegmentServiceCacheTests : IDisposable
{
    private const long From = 1705320000000;
    private const long To = From + 3_600_000;

    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<ITenantAccessor> _tenant = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public BasalSegmentServiceCacheTests() =>
        _tenant.Setup(t => t.TenantId).Returns(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    public void Dispose() => _cache.Dispose();

    private BasalSegmentService Service(bool historyClamped) => new(
        _stateSpans.Object,
        Mock.Of<IBasalScheduleRepository>(),
        Mock.Of<ITherapySettingsRepository>(),
        _tenant.Object,
        _cache,
        Mock.Of<ICategoryReadContext>(c => c.IsHistoryClamped == historyClamped));

    private static async Task DrainAsync(BasalSegmentService service)
    {
        await foreach (var _ in service.GetSegmentsAsync(From, To)) { }
    }

    private void VerifySpanReads(int times) =>
        _stateSpans.Verify(
            s => s.GetStateSpansAsync(
                It.IsAny<StateSpanCategory?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    [Fact]
    public async Task An_unclamped_request_reuses_the_cached_window()
    {
        var service = Service(historyClamped: false);

        await DrainAsync(service);
        await DrainAsync(service);

        VerifySpanReads(1);
    }

    /// <summary>
    /// A history-clamped request reads <c>state_spans</c> narrowed to the last 24 hours, so it must not
    /// serve, or leave behind, the tenant-keyed window another caller reads.
    /// </summary>
    [Fact]
    public async Task A_history_clamped_request_neither_reads_nor_writes_the_cache()
    {
        var clamped = Service(historyClamped: true);
        var unclamped = Service(historyClamped: false);

        await DrainAsync(clamped);
        await DrainAsync(unclamped);
        await DrainAsync(clamped);

        VerifySpanReads(3);
    }
}
