using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Caching;

/// <summary>
/// The write paths of the V4 repositories that do not end in a single create, each reporting the
/// model it wrote to <see cref="ILegacyReadCacheInvalidation"/>. On SQLite, which runs the bulk
/// statements the InMemory provider cannot.
/// </summary>
[Trait("Category", "Unit")]
public class LegacyReadReportingTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly RecordingLegacyReads _written = new();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();
    private readonly IAuditContext _audit = new SystemAuditContext();

    public LegacyReadReportingTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private TempBasalRepository TempBasals() =>
        new(_factory, _dedup, _audit, NullLogger<TempBasalRepository>.Instance, legacyReads: _written);

    private BolusRepository Boluses() =>
        new(_factory, _dedup, _audit, NullLogger<BolusRepository>.Instance, legacyReads: _written);

    private BolusCalculationRepository Calculations() =>
        new(_factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance, legacyReads: _written);

    private MeterGlucoseRepository MeterGlucose() =>
        new(_factory, _audit, NullLogger<MeterGlucoseRepository>.Instance, legacyReads: _written);

    private StateSpanRepository StateSpans() =>
        new(_factory, _dedup, _audit, NullLogger<StateSpanRepository>.Instance, _written);

    private ProfileActivationRepository ProfileActivations() =>
        new(_factory, _audit, NullLogger<ProfileActivationRepository>.Instance, legacyReads: _written);

    private TreatmentFoodRepository TreatmentFoods() => new(_factory, _written);

    private static Bolus Bolus(string? legacyId = null, double insulin = 1) =>
        new() { Timestamp = At, Insulin = insulin, LegacyId = legacyId };

    [Fact]
    public async Task Setting_a_schedule_derived_rate_reports_temp_basals()
    {
        var repository = TempBasals();
        var temp = await repository.CreateAsync(new TempBasal { StartTimestamp = At, Percent = -50 }, WriteOrigin.Live);
        _written.Models.Clear();

        (await repository.SetScheduleDerivedRateAsync(temp.Id, -50, null, 0.5, 1.0)).Should().BeTrue();

        _written.Models.Should().Equal(typeof(TempBasal));
    }

    [Fact]
    public async Task Reconciling_a_sources_window_reports_temp_basals()
    {
        var repository = TempBasals();
        await repository.CreateAsync(
            new TempBasal { StartTimestamp = At, Rate = 0.8, DataSource = "pump", LegacyId = "gone" }, WriteOrigin.Live);
        _written.Models.Clear();

        (await repository.SoftDeleteAbsentBySourceAndDateRangeAsync("pump", At, At, new HashSet<string>())).Should().Be(1);

        _written.Models.Should().Equal(typeof(TempBasal));
    }

    [Fact]
    public async Task A_bulk_delete_too_large_to_broadcast_per_record_reports()
    {
        var repository = MeterGlucose();
        await repository.BulkCreateAsync(
            Enumerable.Range(0, AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1)
                .Select(i => new MeterGlucose { Timestamp = At.AddMinutes(i), Mgdl = 100 }),
            WriteOrigin.Backfill);
        _written.Models.Clear();

        var deleted = await repository.DeleteByTimeRangeAsync(null, null, WriteOrigin.Live);

        deleted.Should().BeGreaterThan(AuditedBulkDeleteExtensions.BroadcastMaterializationCap);
        _written.Models.Should().Equal(typeof(MeterGlucose));
    }

    [Fact]
    public async Task A_restore_reports()
    {
        var repository = Boluses();
        var bolus = await repository.CreateAsync(Bolus(), WriteOrigin.Live);
        await repository.DeleteAsync(bolus.Id, WriteOrigin.Live);
        _written.Models.Clear();

        await repository.RestoreAsync(bolus.Id, WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_restore_reports()
    {
        var repository = Boluses();
        var bolus = await repository.CreateAsync(Bolus(), WriteOrigin.Live);
        await repository.DeleteAsync(bolus.Id, WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkRestoreAsync([bolus.Id], WriteOrigin.Live);

        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_create_reports()
    {
        await Boluses().BulkCreateAsync([Bolus("a"), Bolus("b")], WriteOrigin.Backfill);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_upsert_that_only_updates_reports()
    {
        var repository = Boluses();
        await repository.BulkCreateAsync([Bolus("a")], WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkUpsertAsync([Bolus("a", insulin: 2)], WriteOrigin.Live);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(Bolus));
    }

    [Fact]
    public async Task A_bulk_upsert_by_legacy_id_reports()
    {
        var repository = Calculations();
        await repository.BulkUpsertByLegacyIdAsync(
            [new BolusCalculation { Timestamp = At, LegacyId = "calc", CarbInput = 20 }], WriteOrigin.Live);
        _written.Models.Clear();

        await repository.BulkUpsertByLegacyIdAsync(
            [new BolusCalculation { Timestamp = At, LegacyId = "calc", CarbInput = 30 }], WriteOrigin.Live);

        _written.Models.Should().NotBeEmpty().And.OnlyContain(model => model == typeof(BolusCalculation));
    }

    [Fact]
    public async Task A_create_that_upserts_onto_its_sync_key_reports()
    {
        var repository = Boluses();
        await repository.CreateAsync(
            new Bolus { Timestamp = At, Insulin = 1, DataSource = "pump", SyncIdentifier = "s1" }, WriteOrigin.Live);
        _written.Models.Clear();

        var upsert = await repository.UpsertAsync(
            new Bolus { Timestamp = At, Insulin = 2, DataSource = "pump", SyncIdentifier = "s1" }, WriteOrigin.Live);

        upsert.Created.Should().BeFalse();
        _written.Models.Should().Equal(typeof(Bolus));
    }

    [Fact]
    public async Task Replacing_a_sources_state_spans_replaces_only_its_own_and_reports()
    {
        var repository = StateSpans();
        await repository.ReplaceBySourceAsync("seed", [Span("seed", "old")]);
        await repository.ReplaceBySourceAsync("other", [Span("other", "kept")]);
        _written.Models.Clear();

        await repository.ReplaceBySourceAsync("seed", [Span("seed", "new")]);

        (await _context.StateSpans.Select(s => s.State).OrderBy(state => state).ToListAsync())
            .Should().Equal("kept", "new");
        _written.Models.Should().Equal(typeof(StateSpan));
    }

    [Fact]
    public async Task Replacing_state_spans_with_none_reports_the_rows_it_removed()
    {
        var repository = StateSpans();
        await repository.ReplaceBySourceAsync("seed", [Span("seed", "old")]);
        _written.Models.Clear();

        await repository.ReplaceBySourceAsync("seed", []);

        (await _context.StateSpans.AnyAsync()).Should().BeFalse();
        _written.Models.Should().Equal(typeof(StateSpan));
    }

    [Fact]
    public async Task Replacing_state_spans_that_do_not_exist_reports_nothing()
    {
        await StateSpans().ReplaceBySourceAsync("seed", []);

        _written.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task Replacing_a_sources_profile_activations_replaces_only_its_own_and_reports()
    {
        var repository = ProfileActivations();
        await repository.ReplaceByDataSourceAsync("seed", [Activation("seed", "old")]);
        await repository.ReplaceByDataSourceAsync("other", [Activation("other", "kept")]);
        _written.Models.Clear();

        await repository.ReplaceByDataSourceAsync("seed", [Activation("seed", "new")]);

        (await _context.ProfileActivations.Select(a => a.BaseProfileName).OrderBy(name => name).ToListAsync())
            .Should().Equal("kept", "new");
        _written.Models.Should().Equal(typeof(ProfileActivation));
    }

    [Fact]
    public async Task Replacing_the_food_breakdown_of_an_intake_replaces_only_its_own_and_reports()
    {
        var intakes = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        _context.CarbIntakes.AddRange(intakes.Select(id =>
            new CarbIntakeEntity { Id = id, TenantId = Tenant, Timestamp = At, Carbs = 30 }));
        await _context.SaveChangesAsync();
        var repository = TreatmentFoods();
        await repository.ReplaceForCarbIntakesAsync(
            [intakes[0]], [new TreatmentFood { CarbIntakeId = intakes[0], Portions = 1, Carbs = 10 }]);
        await repository.ReplaceForCarbIntakesAsync(
            [intakes[1]], [new TreatmentFood { CarbIntakeId = intakes[1], Portions = 1, Carbs = 20 }]);
        _written.Models.Clear();

        await repository.ReplaceForCarbIntakesAsync(
            [intakes[0]], [new TreatmentFood { CarbIntakeId = intakes[0], Portions = 2, Carbs = 15 }]);

        (await _context.TreatmentFoods.Select(tf => tf.Carbs).OrderBy(carbs => carbs).ToListAsync())
            .Should().Equal(15, 20);
        _written.Models.Should().Equal(typeof(TreatmentFood));
    }

    [Fact]
    public async Task Stamping_scheduled_rates_stamps_the_unstamped_temps_of_the_source_and_reports()
    {
        var repository = TempBasals();
        await repository.CreateAsync(
            new TempBasal { StartTimestamp = At, Rate = 0.8, DataSource = "seed", Origin = TempBasalOrigin.Algorithm },
            WriteOrigin.Live);
        await repository.CreateAsync(
            new TempBasal { StartTimestamp = At.AddHours(1), Rate = 0.8, DataSource = "other", Origin = TempBasalOrigin.Algorithm },
            WriteOrigin.Live);
        await repository.CreateAsync(
            new TempBasal { StartTimestamp = At.AddHours(2), Rate = 1.0, DataSource = "seed", Origin = TempBasalOrigin.Scheduled },
            WriteOrigin.Live);
        _written.Models.Clear();

        var stamped = await repository.StampScheduledRatesAsync("seed", start => start.Hour);

        stamped.Should().Be(1);
        (await _context.TempBasals.Where(t => t.ScheduledRate != null).Select(t => t.DataSource).ToListAsync())
            .Should().Equal("seed");
        _written.Models.Should().Equal(typeof(TempBasal));
    }

    [Fact]
    public async Task Stamping_scheduled_rates_with_nothing_to_stamp_reports_nothing()
    {
        await TempBasals().StampScheduledRatesAsync("seed", _ => 1);

        _written.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_span_replace_that_fails_while_inserting_keeps_the_purged_spans_and_reports_nothing()
    {
        var repository = StateSpans();
        await repository.ReplaceBySourceAsync("seed", [Span("seed", "old")]);
        _written.Models.Clear();
        var id = Guid.CreateVersion7().ToString();
        var clash = new[] { Span("seed", "new"), Span("seed", "new") };
        clash[0].Id = id;
        clash[1].Id = id;

        await FluentActions.Awaiting(() => repository.ReplaceBySourceAsync("seed", clash))
            .Should().ThrowAsync<Exception>();

        (await _context.StateSpans.Select(s => s.State).ToListAsync()).Should().Equal("old");
        _written.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task A_profile_activation_replace_that_fails_while_inserting_keeps_the_purged_rows_and_reports_nothing()
    {
        var repository = ProfileActivations();
        await repository.ReplaceByDataSourceAsync("seed", [Activation("seed", "old")]);
        _written.Models.Clear();
        var clash = new[] { Activation("seed", "new"), Activation("seed", "new") };
        clash[0].Id = clash[1].Id = Guid.CreateVersion7();

        await FluentActions.Awaiting(() => repository.ReplaceByDataSourceAsync("seed", clash))
            .Should().ThrowAsync<Exception>();

        (await _context.ProfileActivations.Select(a => a.BaseProfileName).ToListAsync()).Should().Equal("old");
        _written.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task A_food_breakdown_replace_that_fails_while_inserting_keeps_the_purged_rows_and_reports_nothing()
    {
        var intake = Guid.CreateVersion7();
        _context.CarbIntakes.Add(new CarbIntakeEntity { Id = intake, TenantId = Tenant, Timestamp = At, Carbs = 30 });
        await _context.SaveChangesAsync();
        var repository = TreatmentFoods();
        await repository.ReplaceForCarbIntakesAsync(
            [intake], [new TreatmentFood { CarbIntakeId = intake, Portions = 1, Carbs = 10 }]);
        _written.Models.Clear();
        var id = Guid.CreateVersion7();
        var clash = new[]
        {
            new TreatmentFood { Id = id, CarbIntakeId = intake, Portions = 1, Carbs = 15 },
            new TreatmentFood { Id = id, CarbIntakeId = intake, Portions = 1, Carbs = 20 },
        };

        await FluentActions.Awaiting(() => repository.ReplaceForCarbIntakesAsync([intake], clash))
            .Should().ThrowAsync<Exception>();

        (await _context.TreatmentFoods.Select(tf => tf.Carbs).ToListAsync()).Should().Equal(10);
        _written.Models.Should().BeEmpty();
    }

    private static StateSpan Span(string source, string state) =>
        new() { Category = StateSpanCategory.Illness, State = state, StartTimestamp = At, Source = source };

    private static ProfileActivation Activation(string dataSource, string profile) =>
        new() { Timestamp = At, BaseProfileName = profile, DataSource = dataSource };
}
