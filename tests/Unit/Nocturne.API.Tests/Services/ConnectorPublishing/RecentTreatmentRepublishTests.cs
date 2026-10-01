using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// A connector publishing its recent treatments again, through the real decomposer and carb store:
/// a record the source has not changed keeps whatever was edited in Nocturne, and one the source
/// has changed is written over it.
/// </summary>
[Trait("Category", "Unit")]
public class RecentTreatmentRepublishTests : IDisposable
{
    private const string Source = "nightscout-connector";

    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly PublishSkipTally _tally = new();
    private readonly TreatmentPublisher _publisher;
    private CarbIntakeRepository _carbIntakes = null!;

    public RecentTreatmentRepublishTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _publisher = NewPublisher();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_in_place_edit_after_the_crawl_imported_the_record_is_applied()
    {
        await CrawlAsync(35);

        var written = await PublishAsync(50);

        written.Should().Be(1, "the crawl stamped what it wrote, so the source's edit shows as a change");
        (await StoredCarbsAsync()).Should().Be(50);
    }

    [Fact]
    public async Task An_edit_made_in_nocturne_survives_repeated_syncs()
    {
        await CrawlAsync(35);
        await EditInNocturneAsync(40);

        await PublishAsync(35);
        await PublishAsync(35);

        (await StoredCarbsAsync()).Should().Be(40);
    }

    [Fact]
    public async Task The_fingerprint_never_reaches_a_record_nocturne_serves()
    {
        await CrawlAsync(35);
        var fingerprint = TreatmentDecomposer.UpstreamFingerprint(Treatment(35));

        var stored = await _context.CarbIntakes.AsNoTracking().SingleAsync(c => c.LegacyId == "t-1");
        stored.UpstreamFingerprint.Should().Be(fingerprint);

        // The record v4 reads, the MCP tools and the v4 broadcasts all serve.
        var served = await _carbIntakes.GetByLegacyIdAsync("t-1");
        System.Text.Json.JsonSerializer.Serialize(served).Should().NotContain(fingerprint);
    }

    [Fact]
    public async Task An_edit_made_at_the_source_is_applied()
    {
        await CrawlAsync(35);
        await EditInNocturneAsync(40);

        var written = await PublishAsync(50);

        written.Should().Be(1);
        (await StoredCarbsAsync()).Should().Be(50, "the source changed the record, so the source wins");
        (await PublishAsync(50)).Should().Be(0);
    }

    [Fact]
    public async Task A_row_stored_before_fingerprints_is_not_overwritten()
    {
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _context.TenantId, LegacyId = "t-1", DataSource = Source,
            Carbs = 40, Timestamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var written = await PublishAsync(35);

        written.Should().Be(0);
        (await StoredCarbsAsync()).Should().Be(40);
    }

    [Fact]
    public async Task A_treatment_the_user_deleted_is_not_attempted()
    {
        _context.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _context.TenantId, LegacyId = "t-1", DataSource = Source,
            Carbs = 35, Timestamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), DeletedAt = DateTime.UtcNow,
        });
        _context.Entry(_context.CarbIntakes.Local.Single()).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var written = await PublishAsync(50);

        written.Should().Be(0);
        _tally.SkippedDeleted.Should().Be(0, "a user deletion is left out before the write, not skipped by it");
        _context.CarbIntakes.Should().BeEmpty();
    }

    private static Treatment Treatment(double carbs) => new()
    {
        Id = "t-1", EventType = "Carb Correction", Carbs = carbs,
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Source,
    };

    private Task<bool> CrawlAsync(double carbs) =>
        _publisher.PublishTreatmentsAsync([Treatment(carbs)], Source, WriteOrigin.Live);

    private Task<int?> PublishAsync(double carbs) =>
        _publisher.PublishRecentTreatmentsAsync([Treatment(carbs)], Source, WriteOrigin.Live);

    /// <summary>The v4 update an edit in the app goes through.</summary>
    private async Task EditInNocturneAsync(double carbs)
    {
        var stored = await _carbIntakes.GetByLegacyIdAsync("t-1");
        stored!.Carbs = carbs;
        await _carbIntakes.UpdateAsync(stored.Id, stored, WriteOrigin.Live);
    }

    private Task<double> StoredCarbsAsync() =>
        _context.CarbIntakes.AsNoTracking().Where(c => c.LegacyId == "t-1").Select(c => c.Carbs).SingleAsync();

    private TreatmentPublisher NewPublisher()
    {
        var ctxFactory = new TestTenantDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var dedup = Mock.Of<IDeduplicationService>();
        var bolus = new BolusRepository(ctxFactory, dedup, audit, NullLogger<BolusRepository>.Instance);
        var carbs = _carbIntakes = new CarbIntakeRepository(ctxFactory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        var bgChecks = new BGCheckRepository(ctxFactory, dedup, audit, NullLogger<BGCheckRepository>.Instance);
        var notes = new NoteRepository(ctxFactory, dedup, audit, NullLogger<NoteRepository>.Instance);
        var deviceEvents = new DeviceEventRepository(ctxFactory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
        var calculations = new BolusCalculationRepository(ctxFactory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance);

        var decomposer = new TreatmentDecomposer(
            _context, bolus, Mock.Of<ITempBasalRepository>(), carbs, bgChecks, notes, deviceEvents, calculations,
            Mock.Of<IStateSpanService>(), Mock.Of<ITreatmentFoodService>(), Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(), Mock.Of<IProfileDecomposer>(), Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(), audit, dedup, NullLogger<TreatmentDecomposer>.Instance);
        var store = new TreatmentReadService(
            Mock.Of<IV4ToLegacyProjectionService>(), decomposer, Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(), bolus, carbs, bgChecks, notes, deviceEvents, calculations,
            NullLogger<TreatmentReadService>.Instance);
        var service = new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);

        return new TreatmentPublisher(
            ctxFactory, service, decomposer, Mock.Of<ITreatmentCache>(),
            bolus, carbs, bgChecks, calculations, Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(), notes, deviceEvents,
            Mock.Of<IPatientInsulinRepository>(), Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(), audit, _tally, NullLogger<TreatmentPublisher>.Instance);
    }
}
