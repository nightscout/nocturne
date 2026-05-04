using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

[Trait("Category", "Unit")]
public class GlucosePublisherTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<ISensorGlucoseRepository> _mockSensorGlucoseRepository;
    private readonly Mock<IPatientDeviceRepository> _mockPatientDeviceRepository;
    private readonly GlucosePublisher _publisher;

    public GlucosePublisherTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockSensorGlucoseRepository = new Mock<ISensorGlucoseRepository>();
        _mockPatientDeviceRepository = new Mock<IPatientDeviceRepository>();

        _publisher = new GlucosePublisher(
            _mockEntryService.Object,
            _mockSensorGlucoseRepository.Object,
            _mockPatientDeviceRepository.Object,
            Mock.Of<IDbContextFactory<NocturneDbContext>>(),
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IAlertOrchestrator>(),
            NullLogger<GlucosePublisher>.Instance
        );
    }

    [Fact]
    public async Task PublishEntriesAsync_DelegatesToEntryService()
    {
        var entries = new List<Entry> { new() { Id = "1" } };
        _mockEntryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _publisher.PublishEntriesAsync(entries, "test-source");

        result.Should().BeTrue();
        _mockEntryService.Verify(
            s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task PublishEntriesAsync_ReturnsFalse_OnException()
    {
        _mockEntryService
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        var result = await _publisher.PublishEntriesAsync(new List<Entry>(), "test-source");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsDate_WhenEntryHasDate()
    {
        var expectedDate = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        _mockEntryService
            .Setup(s => s.GetCurrentEntryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Entry { Date = expectedDate });

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().Be(expectedDate);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsMills_WhenDateIsDefault()
    {
        var mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _mockEntryService
            .Setup(s => s.GetCurrentEntryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Entry { Mills = mills });

        var result = await _publisher.GetLatestEntryTimestampAsync(DataSources.NightscoutConnector);

        result.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime);
    }

    [Fact]
    public async Task GetLatestEntryTimestampAsync_ReturnsNull_WhenNoEntry()
    {
        _mockEntryService
            .Setup(s => s.GetCurrentEntryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entry?)null);

        var result = await _publisher.GetLatestEntryTimestampAsync("test-source");

        result.Should().BeNull();
    }

    // =========================================================================
    // PatientDeviceId stamping tests
    // =========================================================================

    [Fact]
    public async Task PublishSensorGlucoseAsync_StampsPatientDeviceId_WhenMatchingCgmDeviceExists()
    {
        var deviceId = Guid.NewGuid();
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = deviceId,
                    DeviceCategory = DeviceCategory.CGM,
                    Manufacturer = "Dexcom",
                    Model = "Dexcom G7",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 120, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        captured![0].PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_LeavesPatientDeviceIdNull_WhenNoCgmDeviceExists()
    {
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PatientDevice>());

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 120, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured.Should().NotBeNull();
        captured![0].PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_MatchesByManufacturer_AbbottToLibreConnector()
    {
        var deviceId = Guid.NewGuid();
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = deviceId,
                    DeviceCategory = DeviceCategory.CGM,
                    Manufacturer = "Abbott",
                    Model = "FreeStyle Libre 3",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 95, Timestamp = DateTime.UtcNow, DataSource = DataSources.LibreConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.LibreConnector);

        result.Should().BeTrue();
        captured![0].PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_FallsBackToSourceParameter_WhenRecordHasNoDataSource()
    {
        var deviceId = Guid.NewGuid();
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = deviceId,
                    DeviceCategory = DeviceCategory.CGM,
                    Manufacturer = "Dexcom",
                    Model = "Dexcom G7",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 110, Timestamp = DateTime.UtcNow, DataSource = null },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured![0].PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_DoesNotOverwrite_ExistingPatientDeviceId()
    {
        var existingDeviceId = Guid.NewGuid();
        var cgmDeviceId = Guid.NewGuid();

        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = cgmDeviceId,
                    DeviceCategory = DeviceCategory.CGM,
                    Manufacturer = "Dexcom",
                    Model = "Dexcom G7",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 100, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector, PatientDeviceId = existingDeviceId },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured![0].PatientDeviceId.Should().Be(existingDeviceId);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_UsesCatalogManufacturer_WhenCatalogIdIsSet()
    {
        var deviceId = Guid.NewGuid();
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = deviceId,
                    DeviceCategory = DeviceCategory.CGM,
                    Manufacturer = "SomeOldValue",
                    Model = "Dexcom G7",
                    CatalogId = "dexcom-g7",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 130, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured![0].PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_IgnoresNonCgmDevices()
    {
        var pumpId = Guid.NewGuid();
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PatientDevice
                {
                    Id = pumpId,
                    DeviceCategory = DeviceCategory.InsulinPump,
                    Manufacturer = "Insulet",
                    Model = "Omnipod 5",
                    IsCurrent = true,
                },
            });

        List<SensorGlucose>? captured = null;
        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, CancellationToken>((records, _) => captured = records.ToList())
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 100, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        captured![0].PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task PublishSensorGlucoseAsync_ContinuesOnDeviceResolutionFailure()
    {
        _mockPatientDeviceRepository
            .Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        _mockSensorGlucoseRepository
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<SensorGlucose>());

        var records = new List<SensorGlucose>
        {
            new() { Mgdl = 100, Timestamp = DateTime.UtcNow, DataSource = DataSources.DexcomConnector },
        };

        var result = await _publisher.PublishSensorGlucoseAsync(records, DataSources.DexcomConnector);

        result.Should().BeTrue();
        _mockSensorGlucoseRepository.Verify(
            r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
