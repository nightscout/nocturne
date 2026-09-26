using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
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
    public async Task Treatments_the_user_deleted_are_counted_through_the_treatment_service_and_store()
    {
        var decomposer = new Mock<ITreatmentDecomposer>();
        decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { SkippedDeleted = 1 });
        var store = new TreatmentReadService(
            Mock.Of<IV4ToLegacyProjectionService>(), decomposer.Object, Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(), Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(), NullLogger<TreatmentReadService>.Instance);
        var service = new TreatmentService(
            store, decomposer.Object, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);
        var publisher = new TreatmentPublisher(
            new TestTenantDbContextFactory(_context), service, decomposer.Object, Mock.Of<ITreatmentCache>(),
            Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(), Mock.Of<IBGCheckRepository>(),
            Mock.Of<IBolusCalculationRepository>(), Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IPatientInsulinRepository>(), Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(), Mock.Of<IAuditContext>(), _tally,
            NullLogger<TreatmentPublisher>.Instance);

        var published = await publisher.PublishTreatmentsAsync(
            [new Treatment { Id = "t-1", EventType = "Note" }, new Treatment { Id = "t-2", EventType = "Note" }],
            "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.SkippedDeleted.Should().Be(2);
    }

    [Fact]
    public async Task Device_statuses_the_user_deleted_are_counted_through_the_decomposer()
    {
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DecomposeAsync(
                It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult { SkippedDeleted = 1 });
        var publisher = new DevicePublisher(
            decomposer.Object, Mock.Of<IDeviceEventRepository>(), Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IAuditContext>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPatientDeviceRepository>(),
            Mock.Of<IPumpSnapshotRepository>(), Mock.Of<IUploaderSnapshotRepository>(), _tally,
            NullLogger<DevicePublisher>.Instance);

        var published = await publisher.PublishDeviceStatusAsync(
            [new DeviceStatus { Id = "ds-1" }, new DeviceStatus { Id = "ds-2" }], "nightscout", WriteOrigin.Backfill);

        published.Should().BeTrue();
        _tally.SkippedDeleted.Should().Be(2);
    }

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
            entryService, sensorGlucose, meterGlucose, Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<ICanonicalAlertEvaluator>(), audit, _tally, NullLogger<GlucosePublisher>.Instance);
    }
}
