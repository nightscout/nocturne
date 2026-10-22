using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Events;

namespace Nocturne.API.Tests.Events;

[Trait("Category", "Unit")]
public class CompositeDataEventSinkTests
{
    private readonly Mock<IDataEventSink<Entry>> _sink1 = new();
    private readonly Mock<IDataEventSink<Entry>> _sink2 = new();
    private readonly Mock<ILogger<CompositeDataEventSink<Entry>>> _logger = new();
    private readonly CompositeDataEventSink<Entry> _sut;

    public CompositeDataEventSinkTests()
    {
        _sut = new CompositeDataEventSink<Entry>([_sink1.Object, _sink2.Object], _logger.Object);
    }

    [Fact]
    public async Task OnCreatedAsync_Batch_FansOutToAllSinks()
    {
        var entries = new List<Entry> { new() { Id = "1" }, new() { Id = "2" } };

        await _sut.OnCreatedAsync(entries);

        _sink1.Verify(s => s.OnCreatedAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnCreatedAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_Single_FansOutToAllSinks()
    {
        var entry = new Entry { Id = "1" };

        await _sut.OnCreatedAsync(entry);

        _sink1.Verify(s => s.OnCreatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnCreatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnUpdatedAsync_FansOutToAllSinks()
    {
        var entry = new Entry { Id = "1" };

        await _sut.OnUpdatedAsync(entry);

        _sink1.Verify(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BeforeDeleteAsync_FansOutToAllSinks()
    {
        await _sut.BeforeDeleteAsync("abc");

        _sink1.Verify(s => s.BeforeDeleteAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.BeforeDeleteAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnDeletedAsync_FansOutToAllSinks()
    {
        var entry = new Entry { Id = "1" };

        await _sut.OnDeletedAsync(entry);

        _sink1.Verify(s => s.OnDeletedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnDeletedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnWrittenAsync_FansOutToAllSinks()
    {
        var entries = new List<Entry> { new() { Id = "1" } };

        await _sut.OnWrittenAsync(entries);

        _sink1.Verify(s => s.OnWrittenAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnWrittenAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnWrittenAsync_FirstSinkFailure_DoesNotBlockSecondSink()
    {
        var entries = new List<Entry> { new() { Id = "1" } };
        _sink1.Setup(s => s.OnWrittenAsync(entries, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnWrittenAsync(entries);

        _sink2.Verify(s => s.OnWrittenAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnBulkDeletedAsync_FansOutToAllSinks()
    {
        await _sut.OnBulkDeletedAsync(42);

        _sink1.Verify(s => s.OnBulkDeletedAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        _sink2.Verify(s => s.OnBulkDeletedAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_Batch_FirstSinkFailure_DoesNotBlockSecondSink()
    {
        var entries = new List<Entry> { new() { Id = "1" } };
        _sink1.Setup(s => s.OnCreatedAsync(entries, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnCreatedAsync(entries);

        _sink2.Verify(s => s.OnCreatedAsync(entries, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnUpdatedAsync_FirstSinkFailure_DoesNotBlockSecondSink()
    {
        var entry = new Entry { Id = "1" };
        _sink1.Setup(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnUpdatedAsync(entry);

        _sink2.Verify(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnDeletedAsync_FirstSinkFailure_DoesNotBlockSecondSink()
    {
        var entry = new Entry { Id = "1" };
        _sink1.Setup(s => s.OnDeletedAsync(entry, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnDeletedAsync(entry);

        _sink2.Verify(s => s.OnDeletedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SinkFailure_LogsWarning()
    {
        var entries = new List<Entry> { new() { Id = "1" } };
        _sink1.Setup(s => s.OnCreatedAsync(entries, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        await _sut.OnCreatedAsync(entries);

        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task WorksWithoutLogger()
    {
        var sut = new CompositeDataEventSink<Entry>([_sink1.Object, _sink2.Object]);
        var entry = new Entry { Id = "1" };

        _sink1.Setup(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));

        // Should not throw even without a logger
        await sut.OnUpdatedAsync(entry);

        _sink2.Verify(s => s.OnUpdatedAsync(entry, It.IsAny<CancellationToken>()), Times.Once);
    }
}
