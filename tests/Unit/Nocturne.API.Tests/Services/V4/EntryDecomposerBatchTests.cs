using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
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
    private readonly IGlucoseProcessingResolver _glucoseResolver;
    private readonly Mock<IPatientDeviceStamper> _stamperMock = new();
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
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin origin, CancellationToken _) => records);
        _mgRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<MeterGlucose> records, WriteOrigin origin, CancellationToken _) => records);
        _calRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Calibration> records, WriteOrigin origin, CancellationToken _) => records);

        var mockConfigProvider = new Mock<IGlucoseProcessingConfigProvider>();
        mockConfigProvider.Setup(x => x.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        mockConfigProvider.Setup(x => x.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);
        _glucoseResolver = new GlucoseProcessingResolver(mockConfigProvider.Object);

        _decomposer = CreateDecomposer(Mock.Of<IAuditContext>());
    }

    private EntryDecomposer CreateDecomposer(IAuditContext auditContext) =>
        new(
            _context,
            _sgRepoMock.Object,
            _mgRepoMock.Object,
            _calRepoMock.Object,
            _glucoseResolver,
            _stamperMock.Object,
            auditContext,
            NullLogger<EntryDecomposer>.Instance);

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
        var result = await _decomposer.DecomposeBatchAsync(entries, WriteOrigin.Live);

        // Assert — correct partition sizes
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<SensorGlucose>>(list => list.Count() == 2),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<MeterGlucose>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _calRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<Calibration>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(4);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([], WriteOrigin.Live);

        // Assert
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_ProducedRecordsShareCorrelationId()
    {
        // Arrange — multiple entries decomposed in one call
        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 100.0 },
            new() { Id = "sgv2", Type = "sgv", Mills = 1700000001000, Sgv = 110.0 },
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(entries, WriteOrigin.Live);

        // Assert — all produced records share a single non-empty correlation id
        result.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        result.CreatedRecords.OfType<IV4Record>()
            .Should().NotBeEmpty()
            .And.OnlyContain(r => r.CorrelationId == result.CorrelationId);
    }

    /// <summary>
    /// Every entry <em>create</em> reaches this method — v1 and v3 normalize a lone entry object
    /// into a one-element array before calling <c>EntryService.CreateEntriesAsync</c> — so it
    /// carries uploader ingestion (Loop, AAPS, xDrip, connectors, the demo/dev seeder) at CGM
    /// sample rate. Those writes are deliberately not a human mutation trail: the audit
    /// interceptor drops system-attributed saves, and their provenance lives on the records'
    /// own <c>data_source</c>. Only <c>DecomposeAsync</c> — reached solely from
    /// <c>EntryService.UpdateEntryAsync</c>, a genuine per-entry edit — keeps caller attribution.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_WritesUnderSystemAttribution()
    {
        var auditContext = new AuditContext
        {
            SubjectId = Guid.NewGuid(), AuthType = "ApiKey", SubjectName = "uploader",
        };
        var decomposer = CreateDecomposer(auditContext);

        var attributionDuringBulkCreate = new List<(bool IsSystem, Guid? SubjectId)>();
        void Capture() => attributionDuringBulkCreate.Add((auditContext.IsSystem, auditContext.SubjectId));

        _sgRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((IEnumerable<SensorGlucose> records, WriteOrigin _, CancellationToken _) => records);
        _mgRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((IEnumerable<MeterGlucose> records, WriteOrigin _, CancellationToken _) => records);
        _calRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(Capture)
            .ReturnsAsync((IEnumerable<Calibration> records, WriteOrigin _, CancellationToken _) => records);

        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 120.0 },
            new() { Id = "mbg1", Type = "mbg", Mills = 1700000002000, Mbg = 140.0 },
            new() { Id = "cal1", Type = "cal", Mills = 1700000003000, Slope = 850.0 },
        };

        await decomposer.DecomposeBatchAsync(entries, WriteOrigin.Live);

        attributionDuringBulkCreate.Should().HaveCount(3)
            .And.OnlyContain(a => a.IsSystem && a.SubjectId == null);

        auditContext.IsSystem.Should().BeFalse();
        auditContext.SubjectName.Should().Be("uploader");
    }

    /// <summary>
    /// Device attribution matches on each record's own <see cref="IDeviceAttributed.DataSource"/> —
    /// the batch source argument is only the fallback for records that carry none — so a batch
    /// mixing sources must reach the stamper with every record's source intact.
    /// </summary>
    [Fact]
    public async Task DecomposeBatchAsync_StampsRecordsCarryingTheirOwnDataSource()
    {
        // Arrange
        var stamped = new List<IDeviceAttributed>();
        _stamperMock
            .Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
                It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<IDeviceAttributed> records, IReadOnlyList<DeviceCategory> _, string? _, CancellationToken _) =>
                stamped.AddRange(records))
            .Returns(Task.CompletedTask);

        var entries = new List<Entry>
        {
            new() { Id = "sgv1", Type = "sgv", Mills = 1700000000000, Sgv = 120.0, DataSource = DataSources.DexcomConnector },
            new() { Id = "sgv2", Type = "sgv", Mills = 1700000001000, Sgv = 130.0, DataSource = DataSources.LibreConnector },
            new() { Id = "mbg1", Type = "mbg", Mills = 1700000002000, Mbg = 140.0, DataSource = DataSources.GlookoConnector },
        };

        // Act
        await _decomposer.DecomposeBatchAsync(entries, WriteOrigin.Live);

        // Assert
        stamped.Select(r => r.DataSource).Should().BeEquivalentTo(
            [DataSources.DexcomConnector, DataSources.LibreConnector, DataSources.GlookoConnector]);
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
        var result = await _decomposer.DecomposeBatchAsync(entries, WriteOrigin.Live);

        // Assert — only 1 sgv, rawbg skipped
        _sgRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<SensorGlucose>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mgRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<MeterGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _calRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<Calibration>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().HaveCount(1);
    }
}
