using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

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

        // BulkUpsertAsync returns the input records
        _apsRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.ApsSnapshot> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _pumpRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _uploaderRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.UploaderSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.UploaderSnapshot> records, WriteOrigin origin, CancellationToken _) => [.. records]);
        _extrasRepoMock
            .Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceStatusExtras>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.DeviceStatusExtras> records, WriteOrigin origin, CancellationToken _) => [.. records]);

        _decomposer = new DeviceStatusDecomposer(
            _apsRepoMock.Object,
            _pumpRepoMock.Object,
            _uploaderRepoMock.Object,
            _extrasRepoMock.Object,
            _stateSpanServiceMock.Object,
            _deviceServiceMock.Object,
            Mock.Of<IAuditContext>(),
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
        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);

        // Assert
        _apsRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.ApsSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _pumpRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.PumpSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _uploaderRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.UploaderSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(3);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([], source: null, WriteOrigin.Live);

        // Assert
        _apsRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _pumpRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _uploaderRepoMock.Verify(
            x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.UploaderSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _extrasRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<V4Models.DeviceStatusExtras>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_GivesEachStatusItsOwnCorrelationId()
    {
        var statuses = new List<DeviceStatus>
        {
            FullStatus("ds-batch-1", 1700000000000, reservoir: 100.0),
            FullStatus("ds-batch-2", 1700000300000, reservoir: 99.0),
            FullStatus("ds-batch-3", 1700000600000, reservoir: 98.0),
        };

        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);

        var snapshots = result.CreatedRecords.OfType<V4Models.IV4Record>().ToList();
        snapshots.Should().HaveCount(9);
        snapshots.Should().OnlyContain(r => r.CorrelationId.HasValue && r.CorrelationId != Guid.Empty);

        var idsByStatus = snapshots
            .GroupBy(r => r.LegacyId)
            .ToDictionary(g => g.Key!, g => g.Select(r => r.CorrelationId!.Value).Distinct().ToList());
        idsByStatus.Values.Should().OnlyContain(ids => ids.Count == 1, "a status's snapshots are siblings");
        idsByStatus.Values.Select(ids => ids[0]).Should().OnlyHaveUniqueItems("statuses are separate source records");

        var extras = result.CreatedRecords.OfType<V4Models.DeviceStatusExtras>().ToList();
        extras.Should().HaveCount(3, "every status's extras are written, not only the first's");
        foreach (var status in statuses)
        {
            var own = extras.Single(e => e.Timestamp == DateTimeOffset.FromUnixTimeMilliseconds(status.Mills).UtcDateTime);
            own.CorrelationId.Should().Be(idsByStatus[status.Id!][0]);
        }

        result.CorrelationId.Should().Be(idsByStatus["ds-batch-1"][0]);
    }

    [Fact]
    public async Task DecomposeBatchAsync_StatusWhoseSnapshotsAreHeld_WritesNoExtras()
    {
        var held = FullStatus("ds-held", 1700000000000, reservoir: 100.0);
        var fresh = FullStatus("ds-fresh", 1700000300000, reservoir: 99.0);
        _apsRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.ApsSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.ApsSnapshot> records, WriteOrigin _, CancellationToken _) =>
                [.. records.Where(r => r.LegacyId != held.Id)]);
        _pumpRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.PumpSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.PumpSnapshot> records, WriteOrigin _, CancellationToken _) =>
                [.. records.Where(r => r.LegacyId != held.Id)]);
        _uploaderRepoMock
            .Setup(x => x.BulkUpsertAsync(It.IsAny<IEnumerable<V4Models.UploaderSnapshot>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<V4Models.UploaderSnapshot> records, WriteOrigin _, CancellationToken _) =>
                [.. records.Where(r => r.LegacyId != held.Id)]);

        var result = await _decomposer.DecomposeBatchAsync([held, fresh], source: null, WriteOrigin.Backfill);

        var freshCorrelation = result.CreatedRecords.OfType<V4Models.ApsSnapshot>().Single().CorrelationId;
        _extrasRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.DeviceStatusExtras>>(list =>
                    list.Count() == 1 && list.Single().CorrelationId == freshCorrelation),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DecomposeBatchAsync_ProjectedStatusesKeepTheirOwnPumpAndUploader()
    {
        var statuses = new List<DeviceStatus>
        {
            FullStatus("ds-proj-1", 1700000000000, reservoir: 100.0),
            FullStatus("ds-proj-2", 1700000300000, reservoir: 90.0),
            FullStatus("ds-proj-3", 1700000600000, reservoir: 80.0),
        };
        for (var i = 0; i < statuses.Count; i++)
            statuses[i].Uploader!.Battery = 50 + i;

        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);
        var created = result.CreatedRecords;

        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created.OfType<V4Models.ApsSnapshot>().ToList());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created.OfType<V4Models.PumpSnapshot>().ToList());
        pumpRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created.OfType<V4Models.PumpSnapshot>().ToList());
        var uploaderRepo = new Mock<IUploaderSnapshotRepository>();
        uploaderRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created.OfType<V4Models.UploaderSnapshot>().ToList());
        var extrasRepo = new Mock<IDeviceStatusExtrasRepository>();
        extrasRepo
            .Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created.OfType<V4Models.DeviceStatusExtras>().ToList());
        var stateSpanRepo = new Mock<IStateSpanRepository>();
        stateSpanRepo
            .Setup(r => r.GetByCategory(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var projection = new DeviceStatusProjectionService(
            apsRepo.Object, pumpRepo.Object, uploaderRepo.Object, stateSpanRepo.Object, extrasRepo.Object,
            NullLogger<DeviceStatusProjectionService>.Instance);

        var projected = (await projection.GetAsync(10, 0, null, CancellationToken.None)).ToList();

        projected.Should().HaveCount(3);
        foreach (var status in statuses)
        {
            var ds = projected.Single(p => p.Mills == status.Mills);
            ds.Pump!.Reservoir.Should().Be(status.Pump!.Reservoir);
            ds.Uploader!.Battery.Should().Be(status.Uploader!.Battery);
        }
    }

    private static DeviceStatus FullStatus(string id, long mills, double reservoir) => new()
    {
        Id = id,
        Mills = mills,
        Device = "openaps://Samsung",
        OpenAps = new OpenApsStatus { Iob = new OpenApsIobData { Iob = 1.0 } },
        Pump = new PumpStatus { Reservoir = reservoir },
        Uploader = new UploaderStatus { Battery = 70 },
        RadioAdapter = new RadioAdapterStatus { Rssi = -60 },
    };

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
        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);

        // Assert - all three snapshot types extracted from one device status
        _apsRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.ApsSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _pumpRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.PumpSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _uploaderRepoMock.Verify(
            x => x.BulkUpsertAsync(
                It.Is<IEnumerable<V4Models.UploaderSnapshot>>(list => list.Count() == 1),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
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
        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);

        // Assert
        _extrasRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<V4Models.DeviceStatusExtras>>(list =>
                    list.Count() == 1
                    && list.First().Extras!.ContainsKey("xdripjs")),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
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
                    Duration = 3600,
                    Multiplier = 1.5,
                }
            }
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(statuses, source: null, WriteOrigin.Live);

        // Assert
        _stateSpanServiceMock.Verify(
            s => s.UpsertStateSpanAsync(
                It.Is<StateSpan>(ss =>
                    ss.Category == StateSpanCategory.Override
                    && ss.State == "Custom"
                    && ss.OriginalId == "ds-override"
                    && ss.EndTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime.AddHours(1)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().Contain(x => x is StateSpan);
    }
}
