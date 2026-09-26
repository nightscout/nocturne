using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
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
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Trio's lowercase <c>id</c> through the v1 service path an api-secret upload and
/// <c>DELETE /api/v1/treatments?find[id][$eq]=…</c> take: <see cref="TreatmentService"/>, the
/// <see cref="TreatmentReadService"/> store, the legacy-id delete fan-out and the user-tombstone
/// recreation guard. See <see cref="TreatmentClientId"/>.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentClientIdRoundTripTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string CarbId = "0B6F7E4A-1C2D-4E3F-8A9B-0C1D2E3F4A5B";
    private const string EditedCarbId = "C4B3A2F1-0E9D-4C8B-9A7F-6E5D4C3B2A10";
    private const string FpuId = "7D3C2B1A-9E8F-4A6B-8C5D-4E3F2A1B0C9D";
    private const string EditedFpuId = "1A2B3C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D";
    private const string MealTime = "2026-01-01T12:00:00.000Z";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentService _service;
    private readonly CarbIntakeRepository _carbRepo;
    private readonly DeviceEventRepository _deviceEventRepo;

    public TreatmentClientIdRoundTripTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();

        IAuditContext apiSecretCaller = new AuditContext
        {
            SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
            SubjectName = "uploader",
            AuthType = "ApiSecret",
            Endpoint = "DELETE /api/v1/treatments",
        };

        var dedup = new Mock<IDeduplicationService>().Object;
        var ctxFactory = new TestTenantDbContextFactory(_context);
        var bolusRepo = new BolusRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<BolusRepository>.Instance);
        var carbRepo = _carbRepo = new CarbIntakeRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = _deviceEventRepo = new DeviceEventRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(ctxFactory, dedup, apiSecretCaller, NullLogger<BolusCalculationRepository>.Instance);

        var tempBasalRepo = new Mock<ITempBasalRepository>();
        tempBasalRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

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
            _context,
            bolusRepo, tempBasalRepo.Object,
            carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IStateSpanService>(),
            foods.Object,
            devices.Object,
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            apiSecretCaller,
            dedup,
            NullLogger<TreatmentDecomposer>.Instance);

        var projection = new V4ToLegacyProjectionService(
            Mock.Of<ISensorGlucoseRepository>(),
            bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo.Object, bolusCalcRepo,
            foods.Object,
            _context,
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo.Object, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
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

    private static string TrioCarb(string id, int carbs, string createdAt = MealTime, string? note = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["enteredBy"] = "Trio", ["eventType"] = "Carb Correction",
            ["carbs"] = carbs, ["fat"] = 0, ["protein"] = 0, ["notes"] = note,
            ["created_at"] = createdAt,
        });

    private async Task<BulkWrite<Treatment>> UploadAsync(params string[] json) =>
        await _service.CreateTreatmentsAsync(json.Select(j => JsonSerializer.Deserialize<Treatment>(j)!));

    private Task<long> TrioDeleteAsync(string id) => _service.DeleteTreatmentsAsync($"find[id][$eq]={id}");

    private async Task<List<(double? Carbs, string? Id, long Mills)>> CarbsAsync() =>
        (await _service.GetTreatmentsAsync(count: 100))
            .Where(t => t.Carbs is not null)
            .Select(t => (t.Carbs, ClientIdOf(t), t.Mills))
            .ToList();

    private static string? ClientIdOf(Treatment t) =>
        JsonSerializer.SerializeToElement(t).TryGetProperty("id", out var id) ? id.GetString() : null;

    [Fact]
    public async Task Edit_ChangedCarbs_LeavesOnlyTheReplacement()
    {
        await UploadAsync(TrioCarb(CarbId, 30));

        (await TrioDeleteAsync(CarbId)).Should().Be(1);
        (await UploadAsync(TrioCarb(EditedCarbId, 45))).SkippedDeleted.Should().Be(0);

        (await CarbsAsync()).Should().ContainSingle().Which.Should().Be((45d, EditedCarbId, 1767268800000L));
    }

    /// <summary>
    /// A fat/protein-only edit, or an edit saved unchanged: same time, carbs and note, so the
    /// synthetic identity repeats and the delete left a user tombstone on it.
    /// </summary>
    [Fact]
    public async Task Edit_SameValues_IsNotSwallowedByTheUserTombstone()
    {
        await UploadAsync(TrioCarb(CarbId, 30, note: "toast"));
        await TrioDeleteAsync(CarbId);

        await using (var ctx = _db.CreateContext())
        {
            var legacyId = ctx.CarbIntakes.IgnoreQueryFilters().Single().LegacyId!;
            (await ctx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>([legacyId])).DeletedByUser
                .Should().Contain(legacyId, "the api-secret delete must leave a user tombstone for this to test the guard");
        }

        (await UploadAsync(TrioCarb(EditedCarbId, 30, note: "toast"))).SkippedDeleted.Should().Be(0);

        (await CarbsAsync()).Should().ContainSingle().Which.Id.Should().Be(EditedCarbId);
    }

    [Fact]
    public async Task Edit_ThereAndBack_LeavesTheLatestValue()
    {
        const string backId = "9F8E7D6C-5B4A-4392-8170-6F5E4D3C2B1A";
        await UploadAsync(TrioCarb(CarbId, 30));
        await TrioDeleteAsync(CarbId);
        await UploadAsync(TrioCarb(EditedCarbId, 45));
        await TrioDeleteAsync(EditedCarbId);
        (await UploadAsync(TrioCarb(backId, 30))).SkippedDeleted.Should().Be(0);

        (await CarbsAsync()).Should().ContainSingle().Which.Should().Be((30d, backId, 1767268800000L));
    }

    /// <summary>
    /// Trio's fat/protein deletion: one <c>find[id]</c> for the carb equivalents (which all carry the
    /// fpuID), one for the carb, then the regenerated entries under fresh ids at the same times.
    /// </summary>
    [Fact]
    public async Task FatProteinEdit_RegeneratedEquivalents_ReplaceTheOldOnes()
    {
        await UploadAsync(
            TrioCarb(CarbId, 30),
            TrioCarb(FpuId, 10, "2026-01-01T13:00:00.000Z"),
            TrioCarb(FpuId, 10, "2026-01-01T13:30:00.000Z"));
        (await CarbsAsync()).Should().HaveCount(3);

        (await TrioDeleteAsync(FpuId)).Should().Be(2);
        (await TrioDeleteAsync(CarbId)).Should().Be(1);

        (await UploadAsync(
            TrioCarb(EditedCarbId, 30),
            TrioCarb(EditedFpuId, 10, "2026-01-01T13:00:00.000Z"),
            TrioCarb(EditedFpuId, 10, "2026-01-01T13:30:00.000Z"))).SkippedDeleted.Should().Be(0);

        var carbs = await CarbsAsync();
        carbs.Should().HaveCount(3);
        carbs.Select(c => c.Id).Should().BeEquivalentTo([EditedCarbId, EditedFpuId, EditedFpuId]);
    }

    [Fact]
    public async Task BulkCreate_OverAUserTombstone_CreatesOnlyAnotherClientRecord()
    {
        await UploadAsync(TrioCarb(CarbId, 30));
        await TrioDeleteAsync(CarbId);
        var legacyId = TombstonedLegacyId(ctx => ctx.CarbIntakes);

        V4Models.CarbIntake Record(string clientId) => new()
        {
            LegacyId = legacyId,
            Timestamp = DateTime.Parse(MealTime).ToUniversalTime(),
            Carbs = 30,
            AdditionalProperties = new() { [TreatmentClientId.Field] = clientId },
        };

        (await _carbRepo.BulkCreateAsync([Record(CarbId)], WriteOrigin.Live)).SkippedDeleted.Should().Be(1);
        (await _carbRepo.BulkCreateAsync([Record(EditedCarbId)], WriteOrigin.Live)).SkippedDeleted.Should().Be(0);

        (await CarbsAsync()).Should().ContainSingle().Which.Id.Should().Be(EditedCarbId);
    }

    [Fact]
    public async Task BulkUpsertByLegacyId_OverAUserTombstone_CreatesOnlyAnotherClientRecord()
    {
        const string siteChange = """{"id":"{0}","enteredBy":"Trio","eventType":"Site Change","created_at":"2026-01-01T12:00:00.000Z"}""";
        await UploadAsync(siteChange.Replace("{0}", CarbId));
        await TrioDeleteAsync(CarbId);
        var legacyId = TombstonedLegacyId(ctx => ctx.DeviceEvents);

        V4Models.DeviceEvent Record(string clientId) => new()
        {
            LegacyId = legacyId,
            Timestamp = DateTime.Parse(MealTime).ToUniversalTime(),
            EventType = DeviceEventType.SiteChange,
            AdditionalProperties = new() { [TreatmentClientId.Field] = clientId },
        };

        (await _deviceEventRepo.BulkUpsertByLegacyIdAsync([Record(CarbId)], WriteOrigin.Live)).SkippedDeleted.Should().Be(1);
        (await _deviceEventRepo.BulkUpsertByLegacyIdAsync([Record(EditedCarbId)], WriteOrigin.Live)).SkippedDeleted.Should().Be(0);

        var projected = (await _service.GetTreatmentsAsync(count: 10)).Should().ContainSingle().Subject;
        ClientIdOf(projected).Should().Be(EditedCarbId);
    }

    private string TombstonedLegacyId<TEntity>(Func<NocturneDbContext, IQueryable<TEntity>> set)
        where TEntity : V4TimeSeriesEntityBase
    {
        using var ctx = _db.CreateContext();
        return set(ctx).IgnoreQueryFilters().Single().LegacyId!;
    }

    /// <summary>An identity the uploader named is not the content-derived one a Trio edit repeats.</summary>
    [Theory]
    [InlineData("_id", "65a1b2c3d4e5f60718293a4b")]
    [InlineData("syncIdentifier", "uploader-sync-0001")]
    public async Task NamedIdentity_WithANewClientId_StaysDeleted(string field, string identity)
    {
        string Carb(string clientId) => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [field] = identity, ["id"] = clientId, ["enteredBy"] = "Trio",
            ["eventType"] = "Carb Correction", ["carbs"] = 30, ["created_at"] = MealTime,
        });

        await UploadAsync(Carb(CarbId));
        (await TrioDeleteAsync(CarbId)).Should().Be(1);

        (await UploadAsync(Carb(EditedCarbId))).SkippedDeleted.Should().Be(1);

        (await CarbsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ResendOfTheDeletedRecord_StaysDeleted()
    {
        await UploadAsync(TrioCarb(CarbId, 30));
        await TrioDeleteAsync(CarbId);

        (await UploadAsync(TrioCarb(CarbId, 30))).SkippedDeleted.Should().Be(1);

        (await CarbsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RecordDeletedBeforeItsIdWasKept_IsNotRecreatedByAnUploadCarryingOne()
    {
        const string preUpgrade = """{"enteredBy":"Trio","eventType":"Carb Correction","carbs":30,"created_at":"2026-01-01T12:00:00.000Z"}""";
        await UploadAsync(preUpgrade);
        var stored = (await _service.GetTreatmentsAsync(count: 10)).Single();
        (await _service.DeleteTreatmentAsync(stored.Id!)).Should().BeTrue();

        (await UploadAsync(TrioCarb(CarbId, 30))).SkippedDeleted.Should().Be(1);
    }

    [Fact]
    public async Task Put_WithoutId_KeepsTheStoredClientId()
    {
        await UploadAsync(TrioCarb(CarbId, 30));
        var stored = (await _service.GetTreatmentsAsync(count: 10)).Single();

        var replacement = JsonSerializer.Deserialize<Treatment>(
            """{"enteredBy":"Trio","eventType":"Carb Correction","carbs":30,"notes":"edited","created_at":"2026-01-01T12:00:00.000Z"}""")!;
        var wireId = JsonSerializer.SerializeToElement(stored).GetProperty("_id").GetString()!;
        await _service.UpdateTreatmentAsync(wireId, replacement);

        (await CarbsAsync()).Should().ContainSingle().Which.Id.Should().Be(CarbId);
        (await TrioDeleteAsync(CarbId)).Should().Be(1);
    }

    [Fact]
    public async Task ClientIdSharedByEquivalents_IsNotTheLegacyId()
    {
        await UploadAsync(
            TrioCarb(FpuId, 10, "2026-01-01T13:00:00.000Z"),
            TrioCarb(FpuId, 10, "2026-01-01T13:30:00.000Z"));

        await using var ctx = _db.CreateContext();
        var legacyIds = ctx.CarbIntakes.Select(c => c.LegacyId).ToList();
        legacyIds.Should().HaveCount(2).And.OnlyHaveUniqueItems().And.AllSatisfy(id => id.Should().StartWith("syn-"));
    }

    [Fact]
    public async Task ExplicitObjectId_StillWinsOverClientId()
    {
        const string objectId = "65a1b2c3d4e5f60718293a4b";
        await UploadAsync($$"""{"_id":"{{objectId}}","id":"{{CarbId}}","eventType":"Carb Correction","carbs":20,"created_at":"{{MealTime}}"}""");

        await using var ctx = _db.CreateContext();
        ctx.CarbIntakes.Single().LegacyId.Should().Be(objectId);
    }

    [Fact]
    public async Task LoopSyncIdentifier_WithoutClientId_IsUnchanged()
    {
        const string syncIdentifier = "loop-sync-0001";
        await UploadAsync($$"""{"syncIdentifier":"{{syncIdentifier}}","enteredBy":"Loop","eventType":"Correction Bolus","insulin":1.5,"created_at":"{{MealTime}}"}""");

        await using (var ctx = _db.CreateContext())
        {
            var bolus = ctx.Boluses.Single();
            bolus.LegacyId.Should().Be(syncIdentifier);
            bolus.AdditionalPropertiesJson.Should().BeNull();
        }

        var projected = (await _service.GetTreatmentsAsync(count: 10)).Should().ContainSingle().Subject;
        JsonSerializer.SerializeToElement(projected).TryGetProperty("id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task NullClientId_IsNotStored()
    {
        await UploadAsync("""{"id":null,"enteredBy":"Trio","eventType":"Carb Correction","carbs":30,"created_at":"2026-01-01T12:00:00.000Z"}""");

        await using var ctx = _db.CreateContext();
        ctx.CarbIntakes.Single().AdditionalPropertiesJson.Should().BeNull();
    }
}
