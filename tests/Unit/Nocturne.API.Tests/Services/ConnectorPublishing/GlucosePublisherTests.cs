using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

[Trait("Category", "Unit")]
public class GlucosePublisherTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<ISensorGlucoseRepository> _mockSensorGlucoseRepository;
    private readonly Mock<IMeterGlucoseRepository> _mockMeterGlucoseRepository;
    private readonly Mock<IPatientDeviceStamper> _mockPatientDeviceStamper;
    private readonly GlucosePublisher _publisher;

    public GlucosePublisherTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockSensorGlucoseRepository = new Mock<ISensorGlucoseRepository>();
        _mockMeterGlucoseRepository = new Mock<IMeterGlucoseRepository>();
        _mockPatientDeviceStamper = new Mock<IPatientDeviceStamper>();

        _publisher = new GlucosePublisher(
            _mockEntryService.Object,
            _mockSensorGlucoseRepository.Object,
            _mockMeterGlucoseRepository.Object,
            _mockPatientDeviceStamper.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            Mock.Of<IAuditContext>(),
            NullLogger<GlucosePublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishEntriesAsync_DelegatesToEntryService()
    {
        var entries = new List<Entry> { new() { Id = "1" } };
        _mockEntryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _publisher.PublishEntriesAsync(entries, "test-source", WriteOrigin.Live);

        result.Should().BeTrue();
        _mockEntryService.Verify(
            s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishEntriesAsync_ForwardsWriteOrigin_SoBackfillsDoNotBroadcastAsLive()
    {
        // Regression: the publisher dropped its origin, so a first-ever full-history import
        // decomposed everything as Live — broadcasting years of entries to connected clients.
        var entries = new List<Entry> { new() { Id = "1" } };
        _mockEntryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        await _publisher.PublishEntriesAsync(entries, "test-source", WriteOrigin.Backfill);

        _mockEntryService.Verify(
            s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), WriteOrigin.Backfill, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishEntriesAsync_ReturnsFalse_OnException()
    {
        _mockEntryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _publisher.PublishEntriesAsync(new List<Entry>(), "test-source", WriteOrigin.Live);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_WritesThroughRepository_ForPublishedReadings()
    {
        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 120, Timestamp = DateTime.UtcNow.AddMinutes(-5), DataSource = DataSources.DexcomConnector },
            new() { Mgdl = 130, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        // BulkCreateAsync routes through the repository chokepoint, which fires the realtime "entries"
        // broadcast itself — the publisher no longer emits events directly.
        var inserted = new List<SensorGlucose> { records[1] };
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(inserted);

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector, WriteOrigin.Live);

        result.Should().BeTrue();
        _mockSensorGlucoseRepository.Verify(
            r => r.BulkCreateAsync(
                It.Is<IEnumerable<SensorGlucose>>(l => l.Count() == 2),
                It.IsAny<WriteOrigin>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_StampsRecordsAsCgm_BeforeWriting()
    {
        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 120, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector, WriteOrigin.Live);

        result.Should().BeTrue();
        _mockPatientDeviceStamper.Verify(
            s => s.StampAsync(
                It.Is<IReadOnlyList<IDeviceAttributed>>(l => l.Count == 1),
                It.Is<IReadOnlyList<DeviceCategory>>(c => c.Single() == DeviceCategory.CGM),
                DataSources.DexcomConnector,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsSensorTimestamp_WhenPresent()
    {
        var expectedDate = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        _mockSensorGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDate);

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().Be(expectedDate);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_FallsBackToMeterTimestamp_ForManualBgOnlySource()
    {
        var meterDate = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        _mockSensorGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);
        _mockMeterGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(meterDate);

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().Be(meterDate);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsLatestOfSensorAndMeter()
    {
        var sensorDate = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var meterDate = new DateTime(2026, 1, 20, 8, 0, 0, DateTimeKind.Utc);
        _mockSensorGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(sensorDate);
        _mockMeterGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(meterDate);

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().Be(meterDate);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsNull_WhenSourceHasNoData_EvenIfAnotherSourceIsLive()
    {
        // Regression: a tenant migrating from Nightscout while their uploader (e.g. Trio)
        // already pushes glucose directly. The connector has never written a row, so its
        // watermark must be null — a cross-source fallback here made the first sync look
        // incremental and permanently skipped the full-history backfill.
        _mockSensorGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);
        _mockMeterGlucoseRepository
            .Setup(r => r.GetLatestTimestampAsync(DataSources.NightscoutConnector, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);
        _mockEntryService
            .Setup(s => s.GetCurrentEntryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Entry { Date = DateTime.UtcNow });

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().BeNull();
        _mockEntryService.Verify(
            s => s.GetCurrentEntryAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "the watermark must never consult cross-source data");
    }
}
