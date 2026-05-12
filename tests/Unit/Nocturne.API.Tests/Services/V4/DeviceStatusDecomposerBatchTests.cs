using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

public class DeviceStatusDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IApsSnapshotRepository> _apsRepoMock;
    private readonly Mock<IPumpSnapshotRepository> _pumpRepoMock;
    private readonly Mock<IUploaderSnapshotRepository> _uploaderRepoMock;
    private readonly Mock<IDeviceStatusExtrasRepository> _extrasRepoMock;
    private readonly Mock<IStateSpanService> _stateSpanServiceMock;
    private readonly Mock<IDeviceService> _deviceServiceMock;
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _apsRepoMock = new Mock<IApsSnapshotRepository>();
        _pumpRepoMock = new Mock<IPumpSnapshotRepository>();
        _uploaderRepoMock = new Mock<IUploaderSnapshotRepository>();
        _extrasRepoMock = new Mock<IDeviceStatusExtrasRepository>();
        _stateSpanServiceMock = new Mock<IStateSpanService>();
        _deviceServiceMock = new Mock<IDeviceService>();

        // BulkCreateAsync returns the input records
        _apsRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.ApsSnapshot> records, CancellationToken _) => records);
        _pumpRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, CancellationToken _) => records);
        _uploaderRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.UploaderSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.UploaderSnapshot> records, CancellationToken _) => records);
        _extrasRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceStatusExtras>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.DeviceStatusExtras> records, CancellationToken _) => records);

        _decomposer = new DeviceStatusDecomposer(
            _context,
            _apsRepoMock.Object,
            _pumpRepoMock.Object,
            _uploaderRepoMock.Object,
            _extrasRepoMock.Object,
            _stateSpanServiceMock.Object,
            _deviceServiceMock.Object,
            NullLogger<DeviceStatusDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_DecomposesAllSnapshotTypes()
    {
        // Arrange - one OpenAPS, one pump-only, one uploader-only
        var statuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "ds-openaps",
                Mills = 1700000000000,
                Device = "openaps://Samsung",
                OpenAps = new OpenApsStatus
                {
                    Iob = new OpenApsIobData { Iob = 2.0 },
                    Suggested = new OpenApsSuggested
                    {
                        Bg = 120.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                    }
                }
            },
            new()
            {
                Id = "ds-pump",
                Mills = 1700000001000,
                Device = "openaps://Samsung",
                Pump = new PumpStatus
                {
                    Manufacturer = "Insulet",
                    Reservoir = 100.0,
                    Battery = new PumpBattery { Percent = 85 }
                }
            },
            new()
            {
                Id = "ds-uploader",
                Mills = 1700000002000,
                Device = "xDrip+",
                Uploader = new UploaderStatus { Name = "Pixel 8", Battery = 55 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses);

        // Assert
        _apsRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.ApsSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _pumpRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.PumpSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _uploaderRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.UploaderSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([]);

        // Assert
        _apsRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _pumpRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uploaderRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.UploaderSnapshot>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _extrasRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceStatusExtras>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_CreatesDecompositionBatch()
    {
        // Arrange
        var statuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "ds-batch-1",
                Mills = 1700000000000,
                Device = "openaps://Samsung",
                Pump = new PumpStatus { Reservoir = 100.0 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses);

        // Assert
        var batch = _context.DecompositionBatches.SingleOrDefault(b => b.Id == result.CorrelationId);
        batch.Should().NotBeNull();
        batch!.Source.Should().Be("device_status_decomposer_batch");
        batch.SourceRecordId.Should().BeNull();
        batch.TenantId.Should().Be(_context.TenantId);
    }

    [Fact]
    public async Task DecomposeBatchAsync_HandlesMultipleSnapshotTypes()
    {
        // Arrange - single status with pump + APS + uploader
        var statuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "ds-full",
                Mills = 1700000000000,
                Device = "openaps://Samsung",
                OpenAps = new OpenApsStatus
                {
                    Iob = new OpenApsIobData { Iob = 2.0 },
                    Suggested = new OpenApsSuggested
                    {
                        Bg = 110.0, EventualBG = 100.0, Timestamp = "2023-11-14T12:00:00Z"
                    }
                },
                Pump = new PumpStatus
                {
                    Manufacturer = "Medtronic",
                    Reservoir = 100.0,
                    Battery = new PumpBattery { Percent = 90 }
                },
                Uploader = new UploaderStatus { Name = "Pixel 8", Battery = 55 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses);

        // Assert - all three snapshot types extracted from one device status
        _apsRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.ApsSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _pumpRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.PumpSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _uploaderRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.UploaderSnapshot>>(list => list.Count() == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);
        result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.PumpSnapshot>().Should().HaveCount(1);
        result.CreatedRecords.OfType<V4Models.UploaderSnapshot>().Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeBatchAsync_CollectsExtras()
    {
        // Arrange - status with xDripJS data
        var statuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "ds-xdripjs",
                Mills = 1700000000000,
                Device = "xDrip+",
                XDripJs = new XDripJsStatus { State = 6, StateString = "OK", VoltageA = 217, VoltageB = 212 },
                Uploader = new UploaderStatus { Battery = 80 }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses);

        // Assert
        _extrasRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.DeviceStatusExtras>>(list =>
                    list.Count() == 1
                    && list.First().Extras!.ContainsKey("xdripjs")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeBatchAsync_CollectsOverrideStateSpans()
    {
        // Arrange - status with active override
        var expectedStateSpan = new StateSpan
        {
            Id = "ss-override-batch",
            Category = StateSpanCategory.Override,
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
        };

        _stateSpanServiceMock
            .Setup(s => s.UpsertStateSpanAsync(It.IsAny<StateSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStateSpan);

        var statuses = new List<DeviceStatus>
        {
            new()
            {
                Id = "ds-override",
                Mills = 1700000000000,
                Device = "Loop/3.0",
                Override = new OverrideStatus
                {
                    Active = true,
                    Name = "Exercise",
                    Duration = 60.0,
                    Multiplier = 1.5,
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses);

        // Assert
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Override
                    && ss.State == "Custom"
                    && ss.OriginalId == "ds-override"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().Contain(x => x is StateSpan);
    }
}
