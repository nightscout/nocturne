using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class EntryDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<ISensorGlucoseRepository> _sgRepoMock;
    private readonly Mock<IMeterGlucoseRepository> _mgRepoMock;
    private readonly Mock<ICalibrationRepository> _calRepoMock;
    private readonly EntryDecomposer _decomposer;

    public EntryDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _sgRepoMock = new Mock<ISensorGlucoseRepository>();
        _mgRepoMock = new Mock<IMeterGlucoseRepository>();
        _calRepoMock = new Mock<ICalibrationRepository>();

        // BulkCreateAsync returns the input records
        _sgRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<SensorGlucose> records, CancellationToken _) => records);
        _mgRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<MeterGlucose> records, CancellationToken _) => records);
        _calRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Calibration> records, CancellationToken _) => records);

        var mockConfigProvider = new Mock<IGlucoseProcessingConfigProvider>();
        mockConfigProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        mockConfigProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);
        var glucoseResolver = new GlucoseProcessingResolver(mockConfigProvider.Object);

        _decomposer = new EntryDecomposer(
            _context,
            _sgRepoMock.Object,
            _mgRepoMock.Object,
            _calRepoMock.Object,
            glucoseResolver,
            NullLogger<EntryDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_PartitionsByType_CallsBulkCreate()
    {
        // Arrange — 2 sgv + 1 mbg + 1 cal
        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 120.0 },
            new() { Id = "sgv2", Type = "sgv", Mills = 1700000001000, Sgv = 130.0 },
            new() { Id = "mbg1", Type = "mbg", Mills = 1700000002000, Mbg = 140.0 },
            new() { Id = "cal1", Type = "cal", Mills = 1700000003000, Slope = 850.0 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(entries);

        // Assert — correct partition sizes
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<SensorGlucose>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<MeterGlucose>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _calRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<Calibration>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(4);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([]);

        // Assert
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_CreatesDecompositionBatch()
    {
        // Arrange
        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 100.0 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(entries);

        // Assert — a DecompositionBatchEntity was persisted
        var batch = _context.DecompositionBatches.SingleOrDefault(b => b.Id == result.CorrelationId);
        batch.Should().NotBeNull();
        batch!.Source.Should().Be("entry_decomposer_batch");
        batch.SourceRecordId.Should().BeNull();
        batch.TenantId.Should().Be(_context.TenantId);
    }

    [Fact]
    public async Task DecomposeBatchAsync_SkipsUnknownEntryTypes()
    {
        // Arrange — includes an unknown type "rawbg"
        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 100.0 },
            new() { Id = "rawbg1", Type = "rawbg", Mills = 1700000001000 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(entries);

        // Assert — only 1 sgv, rawbg skipped
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<SensorGlucose>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(1);
    }
}
