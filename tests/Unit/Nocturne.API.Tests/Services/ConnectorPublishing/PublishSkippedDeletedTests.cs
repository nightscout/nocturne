using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Services.Entries;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
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
/// A connector re-pulling records the user deleted fetches them and writes none. Each publish path a
/// Nightscout-shaped sync takes has to add what it skipped to the scope's tally, or the sync reports
/// the pull as if every record landed.
/// </summary>
[Trait("Category", "Unit")]
public class PublishSkippedDeletedTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly PublishSkipTally _tally = new();

    public PublishSkippedDeletedTests()
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
    public async Task Entries_the_user_deleted_are_counted_through_the_entry_service_and_decomposer()
    {
        var publisher = RealGlucosePublisher();
        var entry = new Entry { Id = "sgv-deleted", Type = "sgv", Mills = 1700000000000, Sgv = 120 };
        await publisher.PublishEntriesAsync([entry], "nightscout", WriteOrigin.Backfill);
        var stored = _context.SensorGlucose.Single(e => e.LegacyId == "sgv-deleted");
        stored.DeletedAt = DateTime.UtcNow;
        _context.Entry(stored).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var published = await publisher.PublishEntriesAsync(
            [entry, new Entry { Id = "sgv-new", Type = "sgv", Mills = 1700000300000, Sgv = 130 }],
            "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.SkippedDeleted.Should().Be(1);
        _context.SensorGlucose.Count().Should().Be(1, "the new reading lands and the deleted one stays away");
    }

    [Fact]
    public async Task Treatments_the_user_deleted_are_counted_through_the_treatment_service()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkWrite<Treatment>([], skippedDeleted: 2));

        var published = await TreatmentPublisherOver(service).PublishTreatmentsAsync(
            [new Treatment { Id = "t-1", EventType = "Note" }, new Treatment { Id = "t-2", EventType = "Note" }],
            "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.SkippedDeleted.Should().Be(2);
    }

    [Fact]
    public async Task A_treatment_that_can_never_be_stored_does_not_stall_the_publish()
    {
        var refused = new Treatment { Id = "t-1", EventType = "Note" };
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkWrite<Treatment>([new Treatment { Id = "t-2", EventType = "Note" }], 0)
            {
                Rejected = [new BulkWriteRejection<Treatment>(0, refused, "value too long")],
            });

        var published = await TreatmentPublisherOver(service).PublishTreatmentsAsync(
            [refused, new Treatment { Id = "t-2", EventType = "Note" }], "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.Refused.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_treatment_batch_still_counts_what_its_stored_prefix_skipped()
    {
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TreatmentBatchFailedException(
                new BulkWrite<Treatment>([new Treatment { Id = "t-1", EventType = "Note" }], skippedDeleted: 1),
                failedIndex: 1, submitted: 2, failedOneStored: false, new TimeoutException()));

        var published = await TreatmentPublisherOver(service).PublishTreatmentsAsync(
            [new Treatment { Id = "t-1", EventType = "Note" }, new Treatment { Id = "t-2", EventType = "Note" }],
            "nightscout", WriteOrigin.Backfill);

        published.Should().BeFalse();
        _tally.SkippedDeleted.Should().Be(1);
    }

    private TreatmentPublisher TreatmentPublisherOver(Mock<ITreatmentService> service) =>
        new(
            service.Object,
            Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(), Mock.Of<IBGCheckRepository>(),
            Mock.Of<IBolusCalculationRepository>(), Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IPatientInsulinRepository>(), Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(), Mock.Of<IAuditContext>(), _tally,
            Mock.Of<ITenantAccessor>(), NullLogger<TreatmentPublisher>.Instance);

    [Fact]
    public async Task Device_statuses_the_user_deleted_are_counted_through_the_device_status_service()
    {
        var deviceStatuses = new Mock<IDeviceStatusService>();
        deviceStatuses
            .Setup(d => d.CreateDeviceStatusesAsync(
                It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DeviceStatus> statuses, string? _, WriteOrigin _, CancellationToken _) =>
                new BulkWrite<DeviceStatus>(statuses, statuses.Count));
        var publisher = new DevicePublisher(
            deviceStatuses.Object, Mock.Of<IDeviceEventRepository>(), Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IAuditContext>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPumpSnapshotRepository>(), Mock.Of<IUploaderSnapshotRepository>(), _tally,
            Mock.Of<ITenantAccessor>(), NullLogger<DevicePublisher>.Instance);

        var published = await publisher.PublishDeviceStatusAsync(
            [new DeviceStatus { Id = "ds-1" }, new DeviceStatus { Id = "ds-2" }], "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.SkippedDeleted.Should().Be(2);
    }

    private GlucosePublisher RealGlucosePublisher()
    {
        var ctxFactory = new TestNocturneDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var sensorGlucose = new SensorGlucoseRepository(
            ctxFactory, Mock.Of<IDeduplicationService>(), audit, NullLogger<SensorGlucoseRepository>.Instance);
        var meterGlucose = new MeterGlucoseRepository(ctxFactory, audit, NullLogger<MeterGlucoseRepository>.Instance);
        var calibration = new CalibrationRepository(ctxFactory, audit, NullLogger<CalibrationRepository>.Instance);

        var entryService = WritingEntryService.Over(sensorGlucose, meterGlucose, calibration, audit);

        return new GlucosePublisher(
            entryService, sensorGlucose, meterGlucose, calibration, Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ICanonicalAlertEvaluator>(), audit, _tally, Mock.Of<ITenantAccessor>(), NullLogger<GlucosePublisher>.Instance);
    }
}
