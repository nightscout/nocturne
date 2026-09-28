using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// The recent path a catch-up hands records below its cursor to: it writes what nothing stored holds
/// under the record's id, and leaves the rest as stored.
/// </summary>
[Trait("Category", "Unit")]
public class RecentGlucoseAndDeviceStatusPublishTests : IDisposable
{
    private const string Source = "nightscout-connector";

    private readonly NocturneDbContext _context;

    public RecentGlucoseAndDeviceStatusPublishTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Only_entries_nothing_stored_holds_are_written()
    {
        var publisher = RealGlucosePublisher();
        await publisher.PublishEntriesAsync(
            [Sgv("sgv-stored", 1700000000000, 120), Sgv("sgv-deleted", 1700000300000, 125)], Source, WriteOrigin.Live);
        var deleted = _context.SensorGlucose.Single(e => e.LegacyId == "sgv-deleted");
        deleted.DeletedAt = DateTime.UtcNow;
        _context.Entry(deleted).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var written = await publisher.PublishRecentEntriesAsync(
            [
                Sgv("sgv-stored", 1700000000000, 999),
                Sgv("sgv-deleted", 1700000300000, 125),
                Sgv("sgv-backfilled", 1700000600000, 130),
            ],
            Source, WriteOrigin.Live);

        written.Should().Be(1);
        _context.SensorGlucose.Select(e => e.LegacyId).Should().BeEquivalentTo(["sgv-stored", "sgv-backfilled"]);
        _context.SensorGlucose.Single(e => e.LegacyId == "sgv-stored").Mgdl.Should().Be(120,
            "a stored reading is left as it is");
    }

    [Fact]
    public async Task Only_device_statuses_no_snapshot_holds_are_written()
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer.Setup(d => d.HasLegacyKeyedSnapshot(It.IsAny<DeviceStatus>())).Returns(true);
        var decomposed = new List<string?>();
        decomposer
            .Setup(d => d.DecomposeAsync(
                It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceStatus, string?, WriteOrigin, CancellationToken>((ds, _, _, _) => decomposed.Add(ds.Id))
            .ReturnsAsync(new DecompositionResult());
        var pump = new Mock<IPumpSnapshotRepository>();
        pump.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "ds-pump" });
        var uploader = new Mock<IUploaderSnapshotRepository>();
        uploader.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string> { "ds-uploader" });
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        var publisher = DevicePublisher(decomposer.Object, aps.Object, pump.Object, uploader.Object);

        var written = await publisher.PublishRecentDeviceStatusAsync(
            [new DeviceStatus { Id = "ds-pump" }, new DeviceStatus { Id = "ds-uploader" }, new DeviceStatus { Id = "ds-late" }],
            Source, WriteOrigin.Live);

        written.Should().Be(1);
        decomposed.Should().Equal("ds-late");
    }

    [Fact]
    public async Task A_failed_lookup_writes_nothing_and_reports_the_failure()
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer.Setup(d => d.HasLegacyKeyedSnapshot(It.IsAny<DeviceStatus>())).Returns(true);
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic"));
        var publisher = DevicePublisher(
            decomposer.Object, aps.Object, Mock.Of<IPumpSnapshotRepository>(), Mock.Of<IUploaderSnapshotRepository>());

        var written = await publisher.PublishRecentDeviceStatusAsync(
            [new DeviceStatus { Id = "ds-late" }], Source, WriteOrigin.Live);

        written.Should().BeNull();
        decomposer.Verify(d => d.HasLegacyKeyedSnapshot(It.IsAny<DeviceStatus>()));
        decomposer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_device_status_that_yields_no_snapshot_is_not_decomposed()
    {
        var extras = new Mock<IDeviceStatusExtrasRepository>();
        var aps = new Mock<IApsSnapshotRepository>();
        var pump = new Mock<IPumpSnapshotRepository>();
        var uploader = new Mock<IUploaderSnapshotRepository>();
        var decomposer = new DeviceStatusDecomposer(
            aps.Object, pump.Object, uploader.Object, extras.Object, Mock.Of<IStateSpanService>(),
            Mock.Of<IDeviceService>(), Mock.Of<IAuditContext>(), NullLogger<DeviceStatusDecomposer>.Instance);
        var publisher = DevicePublisher(decomposer, aps.Object, pump.Object, uploader.Object);

        var written = await publisher.PublishRecentDeviceStatusAsync(
            [new DeviceStatus { Id = "ds-xdrip", Mills = 1700000000000, Connect = new { }, XDripJs = new XDripJsStatus() }],
            Source, WriteOrigin.Live);

        written.Should().Be(0);
        extras.VerifyNoOtherCalls();
        aps.VerifyNoOtherCalls();
        pump.VerifyNoOtherCalls();
        uploader.VerifyNoOtherCalls();
    }

    private static Entry Sgv(string id, long mills, double sgv) =>
        new() { Id = id, Type = "sgv", Mills = mills, Sgv = sgv, DataSource = Source };

    private static DevicePublisher DevicePublisher(
        IDeviceStatusDecomposer decomposer,
        IApsSnapshotRepository aps,
        IPumpSnapshotRepository pump,
        IUploaderSnapshotRepository uploader) =>
        new(decomposer, Mock.Of<IDeviceEventRepository>(), Mock.Of<IPatientDeviceStamper>(), Mock.Of<IAuditContext>(),
            aps, Mock.Of<IPatientDeviceRepository>(), pump, uploader, new PublishSkipTally(),
            NullLogger<DevicePublisher>.Instance);

    private GlucosePublisher RealGlucosePublisher()
    {
        var ctxFactory = new TestTenantDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var sensorGlucose = new SensorGlucoseRepository(
            ctxFactory, Mock.Of<IDeduplicationService>(), audit, NullLogger<SensorGlucoseRepository>.Instance);
        var meterGlucose = new MeterGlucoseRepository(ctxFactory, audit, NullLogger<MeterGlucoseRepository>.Instance);
        var calibration = new CalibrationRepository(ctxFactory, audit, NullLogger<CalibrationRepository>.Instance);

        var config = new Mock<IGlucoseProcessingConfigProvider>();
        config.Setup(c => c.GetSourceDefaultsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GlucoseProcessingSourceDefault>());
        config.Setup(c => c.GetPreferredProcessingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((GlucoseProcessing?)null);

        var decomposer = new EntryDecomposer(
            _context, sensorGlucose, meterGlucose, calibration, new GlucoseProcessingResolver(config.Object),
            Mock.Of<IPatientDeviceStamper>(), audit, NullLogger<EntryDecomposer>.Instance);
        var entryService = new EntryService(
            Mock.Of<IEntryStore>(), decomposer, Mock.Of<IEntryCache>(), Mock.Of<IDataEventSink<Entry>>(),
            NullLogger<EntryService>.Instance);

        return new GlucosePublisher(
            entryService, sensorGlucose, meterGlucose, calibration, Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ICanonicalAlertEvaluator>(), audit, new PublishSkipTally(), NullLogger<GlucosePublisher>.Instance);
    }
}
