using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A meal posted to v1 with a preBolus is stored as Nightscout's POST stores it: the bolus at its own
/// time with its preBolus, and its carbs as a treatment of their own at created_at + preBolus.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentServicePreBolusSplitTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    private const string NoInsulinMeal =
        """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","timestamp":"2026-09-30T10:00:00Z","carbs":30,"preBolus":15,"notes":"x"}""";

    private const string Repro =
        """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15}""";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentService _service;

    public TreatmentServicePreBolusSplitTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
        _context = _db.CreateContext();

        IAuditContext caller = new AuditContext
        {
            SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
            SubjectName = "uploader",
            AuthType = "ApiSecret",
            Endpoint = "POST /api/v1/treatments",
        };

        var dedup = new Mock<IDeduplicationService>().Object;
        var factory = new TestNocturneDbContextFactory(_context);
        var bolusRepo = new BolusRepository(factory, dedup, caller, NullLogger<BolusRepository>.Instance);
        var carbRepo = new CarbIntakeRepository(factory, dedup, caller, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(factory, dedup, caller, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(factory, dedup, caller, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(factory, dedup, caller, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(factory, dedup, caller, NullLogger<BolusCalculationRepository>.Instance);
        var tempBasalRepo = new TempBasalRepository(factory, dedup, caller, NullLogger<TempBasalRepository>.Instance);

        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var devices = new Mock<IDeviceService>();
        devices
            .Setup(s => s.ResolveAsync(
                It.IsAny<V4Models.DeviceCategory>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var decomposer = new TreatmentDecomposer(
            _context.AsFactory(),
            bolusRepo, tempBasalRepo,
            carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IStateSpanService>(),
            ProfileActivationTestDeps.Repository(_context),
            ProfileActivationTestDeps.Writer(_context),
            foods.Object,
            devices.Object,
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            TempBasalTestDeps.Unscheduled(),
            caller,
            NullLogger<TreatmentDecomposer>.Instance);

        var projection = new V4ToLegacyProjectionService(
            bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo, bolusCalcRepo,
            foods.Object,
            _context.AsFactory(),
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        _service = new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Treatment Parse(string json) => JsonSerializer.Deserialize<Treatment>(json)!;

    private async Task<List<Treatment>> PostAsync(params string[] documents) =>
        [.. await _service.CreateV1TreatmentsAsync(documents.Select(Parse))];

    private async Task<List<Treatment>> ReadAsync() =>
        [.. (await _service.GetTreatmentsAsync(count: 100)).OrderBy(t => t.Mills)];

    private static long Mills(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

    private async Task<(List<V4Models.Bolus> Boluses, List<V4Models.CarbIntake> Carbs)> LiveRowsAsync()
    {
        await using var ctx = _db.CreateContext();
        var boluses = await ctx.Boluses.AsNoTracking().ToListAsync();
        var carbs = await ctx.CarbIntakes.AsNoTracking().ToListAsync();
        return (
            [.. boluses.Select(Nocturne.Infrastructure.Data.Mappers.V4.BolusMapper.ToDomainModel)],
            [.. carbs.Select(Nocturne.Infrastructure.Data.Mappers.V4.CarbIntakeMapper.ToDomainModel)]);
    }

    [Fact]
    public async Task A_meal_with_a_preBolus_stores_the_bolus_at_its_time_and_the_carbs_at_the_offset()
    {
        var posted = await PostAsync(Repro);

        var (boluses, carbs) = await LiveRowsAsync();
        var bolus = boluses.Should().ContainSingle().Subject;
        (bolus.Timestamp, bolus.Insulin).Should().Be((At, 2.0));
        var carb = carbs.Should().ContainSingle().Subject;
        (carb.Timestamp, carb.Carbs).Should().Be((At.AddMinutes(15), 30.0));
        carb.CorrelationId.Should().NotBe(bolus.CorrelationId!.Value);

        posted.Should().HaveCount(2);
        (posted[0].Id, posted[0].Mills, posted[0].Insulin, posted[0].Carbs, posted[0].PreBolus)
            .Should().Be((bolus.Id.ToString(), Mills(At), 2.0, (double?)null, 15.0));
        (posted[1].Id, posted[1].Mills, posted[1].EventType, posted[1].Carbs)
            .Should().Be((carb.Id.ToString(), Mills(At.AddMinutes(15)), "Meal Bolus", 30.0));
    }

    [Fact]
    public async Task A_v1_read_serves_the_bolus_with_its_preBolus_and_the_carbs_as_their_own_treatment()
    {
        await PostAsync(
            """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15,"notes":"pasta"}""");

        var served = await ReadAsync();

        served.Should().HaveCount(2);
        var bolus = served[0];
        (bolus.EventType, bolus.Mills, bolus.Insulin, bolus.Carbs, bolus.PreBolus, bolus.Notes)
            .Should().Be(("Meal Bolus", Mills(At), 2.0, (double?)null, 15.0, (string?)null));
        var carbs = served[1];
        (carbs.EventType, carbs.Mills, carbs.Insulin, carbs.Carbs, carbs.PreBolus, carbs.Notes)
            .Should().Be(("Meal Bolus", Mills(At.AddMinutes(15)), (double?)null, 30.0, (double?)null, "pasta"));
    }

    [Theory]
    [InlineData("""{"_id":"6a1f000000000000000000aa","eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15}""")]
    [InlineData(Repro)]
    public async Task Re_sending_the_same_meal_upserts_both_halves(string document)
    {
        var first = await PostAsync(document);
        var second = await PostAsync(document);

        second.Select(t => t.Id).Should().Equal(first.Select(t => t.Id));
        await using var ctx = _db.CreateContext();
        ctx.Boluses.IgnoreQueryFilters().Should().ContainSingle().Which.DeletedAt.Should().BeNull();
        ctx.CarbIntakes.IgnoreQueryFilters().Should().ContainSingle()
            .Which.DeletedAt.Should().BeNull("a re-send updates the carbs under their derived id rather than replacing them");
        (await ReadAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Re_sending_a_meal_stored_before_the_split_moves_its_carbs_rather_than_doubling_them()
    {
        const string document =
            """{"_id":"6a1f000000000000000000bb","eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15}""";
        await _service.CreateTreatmentsAsync([Parse(document)]);

        await PostAsync(document);

        var (boluses, carbs) = await LiveRowsAsync();
        boluses.Should().ContainSingle().Which.Timestamp.Should().Be(At);
        var carb = carbs.Should().ContainSingle(
            "the bolus half re-sent under the meal's id drops the carbs the meal wrote under it").Subject;
        (carb.Timestamp, carb.Carbs).Should().Be((At.AddMinutes(15), 30.0));
    }

    [Fact]
    public async Task A_negative_preBolus_puts_the_carbs_before_the_bolus()
    {
        await PostAsync(
            """{"eventType":"Snack Bolus","created_at":"2026-09-30T10:00:00Z","carbs":12,"insulin":1,"preBolus":-20}""");

        var (boluses, carbs) = await LiveRowsAsync();
        boluses.Should().ContainSingle().Which.Timestamp.Should().Be(At);
        carbs.Should().ContainSingle().Which.Timestamp.Should().Be(At.AddMinutes(-20));
    }

    [Theory]
    [InlineData("""{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":0}""")]
    [InlineData("""{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2}""")]
    [InlineData("""{"eventType":"Combo Bolus","created_at":"2026-09-30T10:00:00Z","carbs":0,"insulin":2,"preBolus":15}""")]
    [InlineData("""{"eventType":"Carb Correction","created_at":"2026-09-30T10:00:00Z","carbs":30,"preBolus":15}""")]
    public async Task A_treatment_with_nothing_to_split_stays_one_record(string document)
    {
        (await PostAsync(document)).Should().ContainSingle();

        (await ReadAsync()).Should().ContainSingle().Which.Mills.Should().Be(Mills(At));
        var (_, carbs) = await LiveRowsAsync();
        carbs.Should().OnlyContain(c => c.Timestamp == At);
    }

    [Fact]
    public async Task Every_meal_of_an_array_is_split()
    {
        var posted = await PostAsync(
            """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15}""",
            """{"eventType":"Combo Bolus","created_at":"2026-09-30T13:00:00Z","carbs":50,"insulin":4,"preBolus":10}""");

        posted.Select(t => (t.Mills, t.Carbs)).Should().Equal(
            (Mills(At), null),
            (Mills(At.AddMinutes(15)), 30.0),
            (Mills(At.AddHours(3)), null),
            (Mills(At.AddHours(3).AddMinutes(10)), 50.0));
        var (boluses, carbs) = await LiveRowsAsync();
        (boluses.Count, carbs.Count).Should().Be((2, 2));
    }

    [Fact]
    public async Task A_create_outside_v1_keeps_the_carbs_with_the_bolus()
    {
        (await _service.CreateTreatmentsAsync([Parse(Repro)])).Should().ContainSingle();

        var (boluses, carbs) = await LiveRowsAsync();
        boluses.Should().ContainSingle().Which.Timestamp.Should().Be(At);
        carbs.Should().ContainSingle().Which.Timestamp.Should().Be(At);
        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.Carbs, served.Insulin, served.PreBolus).Should().Be((30.0, 2.0, 15.0));
    }

    [Fact]
    public async Task A_split_meal_holds_one_note_on_its_carbs()
    {
        await PostAsync(
            """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15,"notes":"pasta"}""");

        await using (var ctx = _db.CreateContext())
            ctx.Notes.IgnoreQueryFilters().Should().ContainSingle().Which.Text.Should().Be("pasta");
        var served = await ReadAsync();
        served.Single(t => t.Carbs is not null).Notes.Should().Be("pasta");
        served.Single(t => t.Insulin is not null).Notes.Should().BeNull();
    }

    [Fact]
    public async Task A_meal_the_user_deleted_stays_deleted_when_re_sent()
    {
        const string document =
            """{"_id":"6a1f000000000000000000cc","eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":15}""";
        var stored = (await _service.CreateTreatmentsAsync([Parse(document)])).Single();
        (await _service.DeleteTreatmentAsync(stored.Id!)).Should().BeTrue();

        await PostAsync(document);

        (await ReadAsync()).Should().BeEmpty();
        var (boluses, carbs) = await LiveRowsAsync();
        (boluses.Count, carbs.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_meal_with_no_insulin_moves_its_carbs_to_the_offset_as_one_record()
    {
        var posted = await PostAsync(NoInsulinMeal);

        posted.Should().ContainSingle();
        var (boluses, carbs) = await LiveRowsAsync();
        boluses.Should().BeEmpty();
        var carb = carbs.Should().ContainSingle().Subject;
        (carb.Timestamp, carb.Carbs).Should().Be((At.AddMinutes(15), 30.0));
        await using (var ctx = _db.CreateContext())
            ctx.Notes.Should().ContainSingle().Which.Text.Should().Be("x");
        var served = (await ReadAsync()).Should().ContainSingle().Subject;
        (served.EventType, served.Mills, served.Carbs, served.PreBolus, served.Notes)
            .Should().Be(("Meal Bolus", Mills(At.AddMinutes(15)), 30.0, (double?)null, "x"));
        (served.Created_at, served.Timestamp)
            .Should().Be(("2026-09-30T10:15:00.000Z", "2026-09-30T10:15:00.000Z"));
    }

    [Fact]
    public async Task A_no_insulin_meal_read_and_posted_back_keeps_its_carbs_at_the_offset()
    {
        await PostAsync(NoInsulinMeal);
        var served = (await ReadAsync()).Single();

        await PostAsync(JsonSerializer.Serialize(served));

        var (_, carbs) = await LiveRowsAsync();
        carbs.Should().NotBeEmpty().And.OnlyContain(c => c.Timestamp == At.AddMinutes(15));
    }

    [Fact]
    public async Task A_preBolus_beyond_a_day_is_not_applied()
    {
        var posted = await PostAsync(
            """{"eventType":"Meal Bolus","created_at":"2026-09-30T10:00:00Z","carbs":30,"insulin":2,"preBolus":1e300}""");

        posted.Should().ContainSingle();
        var (boluses, carbs) = await LiveRowsAsync();
        boluses.Should().ContainSingle().Which.Timestamp.Should().Be(At);
        carbs.Should().ContainSingle().Which.Timestamp.Should().Be(At);
    }
}
