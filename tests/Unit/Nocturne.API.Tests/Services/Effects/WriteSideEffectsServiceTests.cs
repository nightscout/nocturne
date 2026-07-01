using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Effects;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Tests.Services.Effects;

[Trait("Category", "Unit")]
public class WriteSideEffectsServiceTests
{
    private readonly Mock<ICacheService> _mockCache;
    private readonly Mock<ISignalRBroadcastService> _mockBroadcast;
    private readonly Mock<IDecompositionPipeline> _mockPipeline;
    private readonly Mock<ILogger<WriteSideEffectsService>> _mockLogger;
    private readonly WriteSideEffectsService _service;

    public WriteSideEffectsServiceTests()
    {
        _mockCache = new Mock<ICacheService>();
        _mockBroadcast = new Mock<ISignalRBroadcastService>();
        _mockPipeline = new Mock<IDecompositionPipeline>();
        _mockLogger = new Mock<ILogger<WriteSideEffectsService>>();

        _service = new WriteSideEffectsService(
            _mockCache.Object,
            _mockBroadcast.Object,
            _mockPipeline.Object,
            MockTenantAccessor.Create().Object,
            Enumerable.Empty<ICollectionEffectDescriptor>(),
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task OnCreatedAsync_WithNoOptions_BroadcastsStorageCreate()
    {
        var entries = new List<Entry> { new() { Id = "1", Sgv = 120 } };

        await _service.OnCreatedAsync("entries", entries);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageCreateAsync("entries", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OnCreatedAsync_WithCacheKeys_InvalidatesCache()
    {
        var options = new WriteEffectOptions
        {
            CacheKeysToRemove = ["entries:current:abc"],
            CachePatternsToClear = ["entries:recent:abc:*"]
        };
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries, options);

        _mockCache.Verify(c => c.RemoveAsync("entries:current:abc", It.IsAny<CancellationToken>()), Times.Once);
        _mockCache.Verify(c => c.RemoveByPatternAsync("entries:recent:abc:*", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WithDecomposeToV4_CallsPipeline()
    {
        var options = new WriteEffectOptions { DecomposeToV4 = true };
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries, options);

        _mockPipeline.Verify(
            p => p.DecomposeAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OnCreatedAsync_WithoutDecomposeToV4_SkipsPipeline()
    {
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries);

        _mockPipeline.Verify(
            p => p.DecomposeAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task OnCreatedAsync_WithBroadcastDataUpdate_CallsDataUpdate()
    {
        var options = new WriteEffectOptions { BroadcastDataUpdate = true };
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries, options);

        _mockBroadcast.Verify(
            b => b.BroadcastDataUpdateAsync(It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task BeforeDeleteAsync_WithDecomposeToV4_CallsDeleteByLegacyId()
    {
        var options = new WriteEffectOptions { DecomposeToV4 = true };

        await _service.BeforeDeleteAsync<Entry>("entry-123", options);

        _mockPipeline.Verify(
            p => p.DeleteByLegacyIdAsync<Entry>("entry-123", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OnDeletedAsync_BroadcastsStorageDelete()
    {
        var entry = new Entry { Id = "1" };

        await _service.OnDeletedAsync("entries", entry);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageDeleteAsync("entries", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OnDeletedAsync_WithNullRecord_SkipsBroadcast()
    {
        await _service.OnDeletedAsync<Entry>("entries", null);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }

    [Fact]
    public async Task OnUpdatedAsync_BroadcastsStorageUpdate_AndDecomposes()
    {
        var options = new WriteEffectOptions { DecomposeToV4 = true };
        var entry = new Entry { Id = "1" };

        await _service.OnUpdatedAsync("entries", entry, options);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageUpdateAsync("entries", It.IsAny<object>()),
            Times.Once
        );
        _mockPipeline.Verify(
            p => p.DecomposeAsync(entry, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CacheFailure_DoesNotPreventBroadcast()
    {
        _mockCache
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cache down"));

        var options = new WriteEffectOptions { CacheKeysToRemove = ["key"] };
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries, options);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageCreateAsync("entries", It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task BroadcastFailure_DoesNotPreventDecomposition()
    {
        _mockBroadcast
            .Setup(b => b.BroadcastStorageCreateAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("SignalR down"));

        var options = new WriteEffectOptions { DecomposeToV4 = true };
        var entries = new List<Entry> { new() { Id = "1" } };

        await _service.OnCreatedAsync("entries", entries, options);

        _mockPipeline.Verify(
            p => p.DecomposeAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task OnBulkDeletedAsync_WithZeroCount_DoesNothing()
    {
        await _service.OnBulkDeletedAsync("entries", 0);

        _mockBroadcast.Verify(
            b => b.BroadcastStorageDeleteAsync(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never
        );
    }
}
