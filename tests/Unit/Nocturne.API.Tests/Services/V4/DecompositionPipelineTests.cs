using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class DecompositionPipelineTests
{
    private readonly Mock<IDecomposer<Entry>> _mockEntryDecomposer;
    private readonly DecompositionPipeline _pipeline;

    public DecompositionPipelineTests()
    {
        _mockEntryDecomposer = new Mock<IDecomposer<Entry>>();

        var services = new ServiceCollection();
        services.AddSingleton(_mockEntryDecomposer.Object);
        services.AddSingleton(Mock.Of<ILogger<DecompositionPipeline>>());
        var provider = services.BuildServiceProvider();

        _pipeline = new DecompositionPipeline(provider, provider.GetRequiredService<ILogger<DecompositionPipeline>>());
    }

    [Fact]
    public async Task DecomposeAsync_SingleRecord_ReturnsSucceededCount()
    {
        var entry = new Entry { Id = "test-1", Type = "sgv" };
        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(entry, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        var result = await _pipeline.DecomposeAsync(entry, WriteOrigin.Live);

        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(0);
        result.Results.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeAsync_SingleRecord_WhenDecomposerThrows_ReturnsFailedCount()
    {
        var entry = new Entry { Id = "test-1", Type = "sgv" };
        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(entry, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _pipeline.DecomposeAsync(entry, WriteOrigin.Live);

        result.Succeeded.Should().Be(0);
        result.Failed.Should().Be(1);
        result.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_Batch_ReturnsAggregatedCounts()
    {
        var entries = new[]
        {
            new Entry { Id = "test-1", Type = "sgv" },
            new Entry { Id = "test-2", Type = "mbg" },
            new Entry { Id = "test-3", Type = "cal" }
        };

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(It.IsAny<Entry>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        var result = await _pipeline.DecomposeAsync<Entry>(entries, WriteOrigin.Live);

        result.Succeeded.Should().Be(3);
        result.Failed.Should().Be(0);
        result.Results.Should().HaveCount(3);
    }

    [Fact]
    public async Task DecomposeAsync_Batch_WithMixedResults_TracksSuccessAndFailureSeparately()
    {
        var entries = new[]
        {
            new Entry { Id = "good-1", Type = "sgv" },
            new Entry { Id = "bad-1", Type = "sgv" },
            new Entry { Id = "good-2", Type = "mbg" }
        };

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(
                It.Is<Entry>(e => e.Id!.StartsWith("good")),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(
                It.Is<Entry>(e => e.Id!.StartsWith("bad")),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("decomposition failed"));

        var result = await _pipeline.DecomposeAsync<Entry>(entries, WriteOrigin.Live);

        result.Succeeded.Should().Be(2);
        result.Failed.Should().Be(1);
        result.Results.Should().HaveCount(2);
    }

    [Fact]
    public async Task DecomposeAsync_Batch_ErrorDoesNotStopProcessing()
    {
        var entries = new[]
        {
            new Entry { Id = "bad-1", Type = "sgv" },
            new Entry { Id = "good-1", Type = "sgv" }
        };

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(
                It.Is<Entry>(e => e.Id == "bad-1"),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first fails"));

        _mockEntryDecomposer
            .Setup(x => x.DecomposeAsync(
                It.Is<Entry>(e => e.Id == "good-1"),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { CorrelationId = Guid.NewGuid() });

        var result = await _pipeline.DecomposeAsync<Entry>(entries, WriteOrigin.Live);

        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(1);
        _mockEntryDecomposer.Verify(
            x => x.DecomposeAsync(It.Is<Entry>(e => e.Id == "good-1"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteByLegacyIdAsync_DelegatesToDecomposer()
    {
        _mockEntryDecomposer
            .Setup(x => x.DeleteByLegacyIdAsync("legacy-123", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _pipeline.DeleteByLegacyIdAsync<Entry>("legacy-123", WriteOrigin.Live);

        result.Should().Be(3);
    }

    [Fact]
    public async Task DeleteByLegacyIdAsync_WhenDecomposerThrows_ReturnsZero()
    {
        _mockEntryDecomposer
            .Setup(x => x.DeleteByLegacyIdAsync("legacy-123", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete failed"));

        var result = await _pipeline.DeleteByLegacyIdAsync<Entry>("legacy-123", WriteOrigin.Live);

        result.Should().Be(0);
    }

    [Fact]
    public async Task DecomposeAsync_EmptyBatch_ReturnsZeroCounts()
    {
        var result = await _pipeline.DecomposeAsync<Entry>(Array.Empty<Entry>(), WriteOrigin.Live);

        result.Succeeded.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Results.Should().BeEmpty();
    }
}
