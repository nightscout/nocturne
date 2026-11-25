using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Tests.Services.Treatments;
using Nocturne.API.Tests.Services.V4;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.API.Services.Glucose;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// A connector publishing its recent treatments again, through the real decomposer, carb store and
/// state span store: a record the source has not changed keeps whatever was edited in Nocturne, and
/// one the source has changed is written over it.
/// </summary>
[Trait("Category", "Unit")]
public class RecentTreatmentRepublishTests : IDisposable
{
    private const string Source = "nightscout-connector";

    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly PublishSkipTally _tally = new();
    private readonly Mock<IDataEventSink<Treatment>> _events = new();
    private readonly TreatmentPublisher _publisher;
    private CarbIntakeRepository _carbIntakes = null!;
    private StateSpanRepository _stateSpans = null!;
    private Nocturne.Core.Contracts.Treatments.ITreatmentService _treatments = null!;

    public RecentTreatmentRepublishTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
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

    [Fact]
    public async Task A_shortened_override_is_served_with_its_new_duration()
    {
        await CrawlAsync(Override(60));

        var written = await PublishAsync(Override(30));

        written.Should().Be(1);
        (await ServedAsync()).Duration.Should().Be(30);
        (await StoredSpanAsync()).EndTimestamp.Should().Be(At.AddMinutes(30));
    }

    [Fact]
    public async Task A_shortened_temporary_target_is_served_with_its_new_duration()
    {
        await CrawlAsync(TemporaryTarget(60));

        var written = await PublishAsync(TemporaryTarget(20));

        written.Should().Be(1);
        (await ServedAsync()).Duration.Should().Be(20);
        (await StoredSpanAsync()).EndTimestamp.Should().Be(At.AddMinutes(20));
    }

    [Fact]
    public async Task An_unchanged_override_is_not_written_again()
    {
        await CrawlAsync(Override(60));
        var before = await StoredSpanAsync();

        var written = await PublishAsync(Override(60));

        written.Should().Be(0);
        (await StoredSpanAsync()).UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public async Task An_override_edited_in_nocturne_keeps_the_edit_until_the_source_changes_it()
    {
        await CrawlAsync(Override(60));
        var edited = await _stateSpans.GetStateSpanByIdAsync("ov-1");
        edited!.EndTimestamp = At.AddMinutes(45);
        await _stateSpans.UpdateStateSpanAsync("ov-1", edited);

        (await PublishAsync(Override(60))).Should().Be(0);
        (await StoredSpanAsync()).EndTimestamp.Should().Be(At.AddMinutes(45));

        (await PublishAsync(Override(30))).Should().Be(1);
        (await StoredSpanAsync()).EndTimestamp.Should().Be(At.AddMinutes(30), "the source changed the record, so the source wins");
        (await PublishAsync(Override(30))).Should().Be(0);
    }

    [Fact]
    public async Task An_override_span_stored_before_fingerprints_is_taken_as_it_stands()
    {
        await CrawlAsync(Override(60));
        await _context.StateSpans.Where(s => s.LegacyId == "ov-1")
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.UpstreamFingerprint, (string?)null));

        (await PublishAsync(Override(30))).Should().Be(0);

        var stored = await StoredSpanAsync();
        stored.EndTimestamp.Should().Be(At.AddMinutes(60));
        stored.UpstreamFingerprint.Should().Be(TreatmentDecomposer.UpstreamFingerprint(Override(30)));
    }

    [Fact]
    public async Task A_live_republish_marks_the_span_written_live_and_announces_the_treatment_as_updated()
    {
        await _publisher.PublishTreatmentsAsync([Override(60)], Source, WriteOrigin.Backfill);
        (await StoredSpanAsync()).WrittenLive.Should().BeFalse();

        (await _publisher.PublishRecentTreatmentsAsync([Override(30)], Source, WriteOrigin.Live)).Should().Be(1);

        (await StoredSpanAsync()).WrittenLive.Should().BeTrue();
        _events.Verify(e => e.OnUpdatedAsync(
            It.Is<Treatment>(t => t.LegacyId == "ov-1" && t.Duration == 30), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.OnCreatedAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_backfill_republish_neither_marks_the_span_written_live_nor_announces_it()
    {
        await _publisher.PublishTreatmentsAsync([Override(60)], Source, WriteOrigin.Backfill);

        (await _publisher.PublishRecentTreatmentsAsync([Override(30)], Source, WriteOrigin.Backfill)).Should().Be(1);

        var stored = await StoredSpanAsync();
        stored.EndTimestamp.Should().Be(At.AddMinutes(30));
        stored.WrittenLive.Should().BeFalse();
        _events.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_override_that_also_wrote_a_bolus_is_rewritten_once_per_source_edit()
    {
        await CrawlAsync(OverrideWithBolus(60));
        (await _context.Boluses.AsNoTracking().CountAsync(b => b.LegacyId == "ov-1")).Should().Be(1);

        (await PublishAsync(OverrideWithBolus(30))).Should().Be(1);
        var rewritten = await StoredSpanAsync();

        (await PublishAsync(OverrideWithBolus(30))).Should().Be(0,
            "the span carries the source's fingerprint, whatever its bolus and carbs carry");
        (await StoredSpanAsync()).UpdatedAt.Should().Be(rewritten.UpdatedAt);
    }

    private static readonly DateTime At = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Treatment Override(double duration) => new()
    {
        Id = "ov-1", EventType = "Temporary Override", Duration = duration, ReasonDisplay = "Exercise",
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Source,
    };

    private static Treatment OverrideWithBolus(double duration)
    {
        var treatment = Override(duration);
        treatment.Insulin = 1.5;
        treatment.Carbs = 20;
        return treatment;
    }

    private static Treatment TemporaryTarget(double duration) => new()
    {
        Id = "ov-1", EventType = "Temporary Target", Duration = duration, TargetTop = 140, TargetBottom = 140,
        Units = "mg/dl", Created_at = "2026-03-01T12:00:00.000Z", DataSource = Source,
    };

    private Task<StateSpanEntity> StoredSpanAsync() =>
        _context.StateSpans.AsNoTracking().SingleAsync(s => s.LegacyId == "ov-1");

    /// <summary>The span as the v1 treatments read serves it.</summary>
    private async Task<Treatment> ServedAsync() =>
        (await _treatments.GetTreatmentsAsync(count: 10)).Single(t => t.LegacyId == "ov-1");

    private static Treatment Treatment(double carbs) => new()
    {
        Id = "t-1", EventType = "Carb Correction", Carbs = carbs,
        Created_at = "2026-03-01T12:00:00.000Z", DataSource = Source,
    };

    private Task<bool> CrawlAsync(double carbs) => CrawlAsync(Treatment(carbs));

    private Task<bool> CrawlAsync(Treatment treatment) =>
        _publisher.PublishTreatmentsAsync([treatment], Source, WriteOrigin.Live);

    private Task<int?> PublishAsync(double carbs) => PublishAsync(Treatment(carbs));

    private Task<int?> PublishAsync(Treatment treatment) =>
        _publisher.PublishRecentTreatmentsAsync([treatment], Source, WriteOrigin.Live);

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
        var ctxFactory = new TestNocturneDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var dedup = Mock.Of<IDuplicateLinker>();
        var bolus = new BolusRepository(ctxFactory, dedup, audit, NullLogger<BolusRepository>.Instance);
        var carbs = _carbIntakes = new CarbIntakeRepository(ctxFactory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        var bgChecks = new BGCheckRepository(ctxFactory, dedup, audit, NullLogger<BGCheckRepository>.Instance);
        var notes = new NoteRepository(ctxFactory, dedup, audit, NullLogger<NoteRepository>.Instance);
        var deviceEvents = new DeviceEventRepository(ctxFactory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
        var calculations = new BolusCalculationRepository(ctxFactory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance);

        var decomposer = TreatmentDecomposerDoubles.Build(
            _context.AsFactory(),
            bolus: bolus, tempBasal: EmptyLegacyKeyed.Of<ITempBasalRepository, Nocturne.Core.Models.V4.TempBasal>(),
            carbIntake: carbs, bgCheck: bgChecks, note: notes, deviceEvent: deviceEvents, bolusCalculation: calculations,
            stateSpans: new StateSpanService(
                _stateSpans = new StateSpanRepository(ctxFactory, dedup, audit, NullLogger<StateSpanRepository>.Instance),
                NullLogger<StateSpanService>.Instance),
            audit: audit);
        var service = _treatments = WritingTreatmentService.Over(
            decomposer, Mock.Of<ITempBasalRepository>(), bolus, carbs, bgChecks, notes, deviceEvents, calculations,
            contexts: ctxFactory, events: _events.Object);

        return new TreatmentPublisher(
            service,
            bolus, carbs, bgChecks, calculations, Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(), notes, deviceEvents,
            Mock.Of<IPatientInsulinRepository>(), Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(), audit, _tally, Mock.Of<ITenantAccessor>(), NullLogger<TreatmentPublisher>.Instance);
    }
}
