using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Tests for <see cref="TreatmentService.DeleteTreatmentsAsync"/>: field-filtered deletes must
/// resolve matches through the read path and delete individually (never the coarse by-time sweep,
/// which would remove every record type in the window), while pure time-range deletes still
/// delegate to the decomposer sweep.
/// </summary>
public class TreatmentServiceBulkDeleteTests
{
    private readonly Mock<ITreatmentStore> _store = new();
    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<ITreatmentCache> _cache = new();
    private readonly Mock<IDataEventSink<Treatment>> _events = new();
    private readonly TreatmentService _service;

    public TreatmentServiceBulkDeleteTests()
    {
        _service = new TreatmentService(
            _store.Object, _decomposer.Object, _cache.Object, _events.Object,
            new Mock<IPatientInsulinRepository>().Object,
            NullLogger<TreatmentService>.Instance);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_EventTypeFilter_DeletesOnlyMatchingRecords()
    {
        // The data-loss case: find[eventType]=Note&find[created_at][$gte]=X must not touch
        // boluses and carbs in the same window
        var find = "find[eventType]=Note&find[created_at][$gte]=2023-01-01T00:00:00.000Z";
        var matching = new List<Treatment>
        {
            new() { Id = "note-1", Mills = 1672531200001, EventType = "Note" },
            new() { Id = "note-2", Mills = 1672531200002, EventType = "Note" },
        };

        _store
            .Setup(s => s.QueryAsync(
                It.Is<TreatmentQuery>(q => q.Find == find), It.IsAny<CancellationToken>()))
            .ReturnsAsync(matching);
        _store
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var deleted = await _service.DeleteTreatmentsAsync(find);

        deleted.Should().Be(2);
        _store.Verify(s => s.DeleteAsync("note-1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync("note-2", It.IsAny<CancellationToken>()), Times.Once);
        _decomposer.Verify(
            d => d.BulkDeleteAsync(It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _events.Verify(e => e.OnDeletedAsync(It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_CreatedAtEq_DelegatesToTimeRangeSweep()
    {
        // Trio's remote delete: find[created_at][$eq]=<iso> is a pure (exact) time bound, so the
        // decomposer sweep handles it and removes the record plus its correlated siblings
        var find = "find[created_at][$eq]=2023-01-01T10:30:00.000Z";
        _decomposer
            .Setup(d => d.BulkDeleteAsync(find, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var deleted = await _service.DeleteTreatmentsAsync(find);

        deleted.Should().Be(1);
        _decomposer.Verify(d => d.BulkDeleteAsync(find, WriteOrigin.Live, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.QueryAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_TimeRangeOnly_DelegatesToSweep()
    {
        var find = "find[created_at][$gte]=2023-01-01T00:00:00Z&find[created_at][$lte]=2023-01-02T00:00:00Z";
        _decomposer
            .Setup(d => d.BulkDeleteAsync(find, WriteOrigin.Live, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var deleted = await _service.DeleteTreatmentsAsync(find);

        deleted.Should().Be(5);
        _store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTreatmentsAsync_NoMatches_ReturnsZeroWithoutInvalidation()
    {
        _store
            .Setup(s => s.QueryAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Treatment>());

        var deleted = await _service.DeleteTreatmentsAsync("find[eventType]=Note&find[created_at][$gte]=2023-01-01T00:00:00Z");

        deleted.Should().Be(0);
        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
