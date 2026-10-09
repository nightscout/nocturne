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
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// Rows a connector stored under an uploader's own <c>id</c> are moved onto the treatment's
/// <c>_id</c> when that treatment arrives again, through the real decomposer and carb store. Trio
/// stamps every carb equivalent of one fat/protein entry with the same <c>id</c>, so those rows hold
/// one of several equivalents.
/// </summary>
[Trait("Category", "Unit")]
public class ClientIdRekeyTests : IDisposable
{
    private const string Source = "nightscout-connector";
    private const string TrioId = "B5E5A1C2-0000-4000-8000-000000000001";
    private const string ObjectIdA = "6ab400000000000000000001";
    private const string ObjectIdB = "6ab400000000000000000002";
    private const string ObjectIdC = "6ab400000000000000000003";

    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime MealAt = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentPublisher _publisher;

    public ClientIdRekeyTests()
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
    public async Task Carb_equivalents_stored_as_one_row_are_split_into_one_row_each()
    {
        var stored = await StoreAsync(TrioId, Source, carbs: 12, at: MealAt.AddHours(3));

        await _publisher.PublishTreatmentsAsync(
        [
            Equivalent(ObjectIdA, 10, MealAt.AddHours(1)),
            Equivalent(ObjectIdB, 11, MealAt.AddHours(2)),
            Equivalent(ObjectIdC, 12, MealAt.AddHours(3)),
        ], Source, WriteOrigin.Live);

        var live = await _context.CarbIntakes.AsNoTracking().OrderBy(c => c.LegacyId).ToListAsync();
        live.Select(c => (c.LegacyId, c.Carbs)).Should().Equal((ObjectIdA, 10), (ObjectIdB, 11), (ObjectIdC, 12));
        live.Single(c => c.Id == stored).LegacyId.Should().Be(ObjectIdC,
            "the stored row moves to the equivalent at its time, not beside a copy");
    }

    [Fact]
    public async Task A_merged_row_the_user_deleted_blocks_every_equivalent()
    {
        await StoreAsync(TrioId, Source, carbs: 12, at: MealAt.AddHours(3), deletedByUser: true);

        await _publisher.PublishTreatmentsAsync(
        [
            Equivalent(ObjectIdA, 10, MealAt.AddHours(1)),
            Equivalent(ObjectIdB, 11, MealAt.AddHours(2)),
            Equivalent(ObjectIdC, 12, MealAt.AddHours(3)),
        ], Source, WriteOrigin.Live);

        _context.CarbIntakes.Should().BeEmpty();
        var tombstones = await _context.CarbIntakes.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        tombstones.Select(c => c.LegacyId).Should().BeEquivalentTo([TrioId, ObjectIdA, ObjectIdB, ObjectIdC]);
    }

    [Fact]
    public async Task A_merged_row_the_user_deleted_blocks_an_equivalent_published_later()
    {
        await StoreAsync(TrioId, Source, carbs: 12, at: MealAt.AddHours(3), deletedByUser: true);

        await _publisher.PublishTreatmentsAsync(
        [
            Equivalent(ObjectIdA, 10, MealAt.AddHours(1)),
            Equivalent(ObjectIdB, 11, MealAt.AddHours(2)),
        ], Source, WriteOrigin.Live);
        await _publisher.PublishTreatmentsAsync(
            [Equivalent(ObjectIdC, 12, MealAt.AddHours(3))], Source, WriteOrigin.Live);
        await _publisher.PublishTreatmentsAsync(
            [Equivalent(ObjectIdC, 12, MealAt.AddHours(3))], Source, WriteOrigin.Live);

        _context.CarbIntakes.Should().BeEmpty();
        var tombstones = await _context.CarbIntakes.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        tombstones.Select(c => c.LegacyId).Should().BeEquivalentTo([TrioId, ObjectIdA, ObjectIdB, ObjectIdC],
            "a copy already held is not copied again");
    }

    [Fact]
    public async Task A_state_span_the_user_deleted_blocks_an_equivalent_published_later()
    {
        var span = new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _context.TenantId, Category = "Override", State = "Active",
            StartTimestamp = MealAt, Source = Source, OriginalId = TrioId, DeletedAt = DateTime.UtcNow,
        };
        _context.StateSpans.Add(span);
        _context.Entry(span).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _publisher.PublishTreatmentsAsync(
        [
            Equivalent(ObjectIdA, 10, MealAt.AddHours(1)),
            Equivalent(ObjectIdB, 11, MealAt.AddHours(2)),
        ], Source, WriteOrigin.Live);
        await _publisher.PublishTreatmentsAsync(
            [Equivalent(ObjectIdC, 12, MealAt.AddHours(3))], Source, WriteOrigin.Live);

        var spans = await _context.StateSpans.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        spans.Should().OnlyContain(s => s.DeletedAt != null);
        spans.Select(s => s.OriginalId).Should().BeEquivalentTo([TrioId, ObjectIdA, ObjectIdB, ObjectIdC]);
    }

    [Fact]
    public async Task A_row_the_user_deleted_keeps_blocking_under_the_new_id()
    {
        await StoreAsync(TrioId, Source, carbs: 10, at: MealAt, deletedByUser: true);

        await _publisher.PublishTreatmentsAsync([Equivalent(ObjectIdA, 10, MealAt)], Source, WriteOrigin.Live);

        _context.CarbIntakes.Should().BeEmpty();
        var tombstones = await _context.CarbIntakes.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        tombstones.Select(c => c.LegacyId).Should().BeEquivalentTo([TrioId, ObjectIdA]);
    }

    [Fact]
    public async Task Another_sources_row_under_the_same_id_is_left_alone()
    {
        await StoreAsync(TrioId, "other-uploader", carbs: 10, at: MealAt);

        await _publisher.PublishTreatmentsAsync([Equivalent(ObjectIdA, 10, MealAt)], Source, WriteOrigin.Live);

        var live = await _context.CarbIntakes.AsNoTracking().ToListAsync();
        live.Select(c => (c.LegacyId, c.DataSource)).Should().BeEquivalentTo(
            [(TrioId, "other-uploader"), (ObjectIdA, Source)]);
    }

    [Fact]
    public async Task An_id_already_stored_is_not_merged_into()
    {
        await StoreAsync(TrioId, Source, carbs: 11, at: MealAt.AddHours(1));
        await StoreAsync(ObjectIdA, Source, carbs: 10, at: MealAt);

        await _publisher.PublishTreatmentsAsync([Equivalent(ObjectIdA, 10, MealAt)], Source, WriteOrigin.Live);

        var live = await _context.CarbIntakes.AsNoTracking().ToListAsync();
        live.Select(c => c.LegacyId).Should().BeEquivalentTo([TrioId, ObjectIdA]);
    }

    private static Treatment Equivalent(string objectId, double carbs, DateTime at) => new()
    {
        Id = objectId, EventType = "Carb Correction", Carbs = carbs, EnteredBy = "Trio",
        Created_at = at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), DataSource = Source,
        AdditionalProperties = new() { [TreatmentClientId.Field] = TrioId },
    };

    private async Task<Guid> StoreAsync(
        string legacyId, string source, double carbs, DateTime at, bool deletedByUser = false)
    {
        var row = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _context.TenantId, LegacyId = legacyId, DataSource = source,
            Carbs = carbs, Timestamp = at, DeletedAt = deletedByUser ? DateTime.UtcNow : null,
        };
        _context.CarbIntakes.Add(row);
        if (deletedByUser)
            _context.Entry(row).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return row.Id;
    }

    private TreatmentPublisher NewPublisher()
    {
        var ctxFactory = new TestTenantDbContextFactory(_context);
        var audit = Mock.Of<IAuditContext>();
        var dedup = Mock.Of<IDeduplicationService>();
        var bolus = new BolusRepository(ctxFactory, dedup, audit, NullLogger<BolusRepository>.Instance);
        var carbs = new CarbIntakeRepository(ctxFactory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        var bgChecks = new BGCheckRepository(ctxFactory, dedup, audit, NullLogger<BGCheckRepository>.Instance);
        var notes = new NoteRepository(ctxFactory, dedup, audit, NullLogger<NoteRepository>.Instance);
        var deviceEvents = new DeviceEventRepository(ctxFactory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
        var calculations = new BolusCalculationRepository(ctxFactory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance);

        var decomposer = new TreatmentDecomposer(
            _context, bolus, Mock.Of<ITempBasalRepository>(), carbs, bgChecks, notes, deviceEvents, calculations,
            Mock.Of<IStateSpanService>(), Mock.Of<ITreatmentFoodService>(), Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(), Mock.Of<IProfileDecomposer>(), Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(), audit, NullLogger<TreatmentDecomposer>.Instance);
        var store = new TreatmentReadService(
            Mock.Of<IV4ToLegacyProjectionService>(), decomposer, Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(), bolus, carbs, bgChecks, notes, deviceEvents, calculations,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);
        var service = new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);

        return new TreatmentPublisher(
            ctxFactory, service, decomposer, Mock.Of<ITreatmentCache>(),
            bolus, carbs, bgChecks, calculations, Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(), notes, deviceEvents,
            Mock.Of<IPatientInsulinRepository>(), Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(), audit, new PublishSkipTally(), NullLogger<TreatmentPublisher>.Instance);
    }
}
