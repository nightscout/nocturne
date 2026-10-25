using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Legacy;
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
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// POST /api/v1/treatments through the real controller, service, store and decomposer. A document
/// with no <c>_id</c>, <c>identifier</c> or <c>syncIdentifier</c> replaces the stored treatment at its
/// <c>created_at</c> with its <c>eventType</c>, as Nightscout's upsert does.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsPostUpsertTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string At = "2026-09-30T10:00:00.000Z";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TreatmentService _service;
    private readonly TreatmentsController _controller;

    public TreatmentsPostUpsertTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
        _context = _db.CreateContext();

        var audit = Mock.Of<IAuditContext>();
        var dedup = new Mock<IDeduplicationService>().Object;
        var ctxFactory = new TestNocturneDbContextFactory(_context);
        var bolusRepo = new BolusRepository(ctxFactory, dedup, audit, NullLogger<BolusRepository>.Instance);
        var carbRepo = new CarbIntakeRepository(ctxFactory, dedup, audit, NullLogger<CarbIntakeRepository>.Instance);
        var bgCheckRepo = new BGCheckRepository(ctxFactory, dedup, audit, NullLogger<BGCheckRepository>.Instance);
        var noteRepo = new NoteRepository(ctxFactory, dedup, audit, NullLogger<NoteRepository>.Instance);
        var deviceEventRepo = new DeviceEventRepository(ctxFactory, dedup, audit, NullLogger<DeviceEventRepository>.Instance);
        var bolusCalcRepo = new BolusCalculationRepository(ctxFactory, dedup, audit, NullLogger<BolusCalculationRepository>.Instance);

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
            _context.AsFactory(),
            bolusRepo, tempBasalRepo.Object,
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
            audit,
            NullLogger<TreatmentDecomposer>.Instance);

        var projection = new V4ToLegacyProjectionService(
            bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo,
            tempBasalRepo.Object, bolusCalcRepo,
            foods.Object,
            _context.AsFactory(),
            NullLogger<V4ToLegacyProjectionService>.Instance);

        var pipeline = new DecompositionPipeline(
            new ServiceCollection().AddSingleton<IDecomposer<Treatment>>(decomposer).BuildServiceProvider(),
            NullLogger<DecompositionPipeline>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer, pipeline,
            tempBasalRepo.Object, bolusRepo, carbRepo, bgCheckRepo, noteRepo, deviceEventRepo, bolusCalcRepo,
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        _service = new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);

        _controller = new TreatmentsController(
            _service,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Post_WithNoIdentity_ReplacesTheTreatmentAtTheSameTimeAndEventType()
    {
        await PostAsync(Carbs("Carb Correction", 30));
        await PostAsync(Carbs("Carb Correction", 45));

        (await StoredAsync()).Should().ContainSingle().Which.Carbs.Should().Be(45);
    }

    [Fact]
    public async Task PostArray_WithNoIdentity_ReplacesTheTreatmentAtTheSameTimeAndEventType()
    {
        await PostAsync(Carbs("Carb Correction", 30));
        await PostAsync(new[] { Carbs("Carb Correction", 45) });

        (await StoredAsync()).Should().ContainSingle().Which.Carbs.Should().Be(45);
    }

    [Fact]
    public async Task PostArray_WithTwoDocumentsOfOneKey_KeepsTheLast()
    {
        await PostAsync(new[] { Carbs("Carb Correction", 30), Carbs("Carb Correction", 45) });

        (await StoredAsync()).Should().ContainSingle().Which.Carbs.Should().Be(45);
    }

    [Fact]
    public async Task Post_AfterTheUserDeletedTheTreatment_StoresTheNewValue()
    {
        await PostAsync(Carbs("Carb Correction", 30));
        var stored = (await StoredAsync()).Single();
        (await _service.DeleteTreatmentAsync(stored.Id!)).Should().BeTrue();

        await PostAsync(Carbs("Carb Correction", 45));

        (await StoredAsync()).Should().ContainSingle().Which.Carbs.Should().Be(45);
    }

    [Fact]
    public async Task Post_ReplacingAMealWithOneWithoutCarbs_RemovesTheOldCarbs()
    {
        await PostAsync(new { eventType = "Meal Bolus", created_at = At, insulin = 2, carbs = 30 });
        await PostAsync(new { eventType = "Meal Bolus", created_at = At, insulin = 3 });

        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.Insulin.Should().Be(3);
        stored.Carbs.Should().BeNull();
    }

    [Fact]
    public async Task Post_WithADifferentEventTypeAtTheSameTime_KeepsBoth()
    {
        await PostAsync(Carbs("Carb Correction", 30));
        await PostAsync(new { eventType = "Correction Bolus", created_at = At, insulin = 2 });

        var stored = await StoredAsync();
        stored.Select(t => t.EventType).Should().BeEquivalentTo(["Carb Correction", "Correction Bolus"]);
    }

    [Fact]
    public async Task Post_CorrectingACarbsOnlyMealBolusTwice_KeepsOneRow()
    {
        await PostAsync(Carbs("Meal Bolus", 30));
        await PostAsync(Carbs("Meal Bolus", 45));
        await PostAsync(Carbs("Meal Bolus", 50));

        (await StoredAsync()).Should().ContainSingle().Which.Carbs.Should().Be(50);
    }

    [Fact]
    public async Task Post_CorrectingASnackBolus_ReplacesIt()
    {
        await PostAsync(new { eventType = "Snack Bolus", created_at = At, insulin = 2, carbs = 30 });
        await PostAsync(new { eventType = "Snack Bolus", created_at = At, insulin = 3, carbs = 30 });

        (await StoredAsync()).Should().ContainSingle().Which.Insulin.Should().Be(3);
    }

    [Fact]
    public async Task Post_CorrectingABolus_ReplacesIt()
    {
        await PostAsync(new { eventType = "Bolus", created_at = At, insulin = 2 });
        await PostAsync(new { eventType = "Bolus", created_at = At, insulin = 3 });

        (await StoredAsync()).Should().ContainSingle().Which.Insulin.Should().Be(3);
    }

    [Fact]
    public async Task Post_CarbCorrectionAtTheTimeOfACarbsOnlyMealBolus_KeepsBothBecauseEachIsServedAsUploaded()
    {
        await PostAsync(Carbs("Meal Bolus", 30));
        await PostAsync(Carbs("Carb Correction", 45));

        var stored = await StoredAsync();
        stored.Should().HaveCount(2);
        stored.Single(t => t.EventType == "Meal Bolus").Carbs.Should().Be(30);
        stored.Single(t => t.EventType == "Carb Correction").Carbs.Should().Be(45);
    }

    [Fact]
    public async Task Post_WithSeveralTreatmentsAtOneTime_ReplacesTheOneOfTheSameType()
    {
        await PostAsync(Carbs("Carb Correction", 30));
        await PostAsync(new { eventType = "Correction Bolus", created_at = At, insulin = 2 });

        await PostAsync(new { eventType = "Correction Bolus", created_at = At, insulin = 3 });

        var stored = await StoredAsync();
        stored.Should().HaveCount(2);
        stored.Single(t => t.EventType == "Carb Correction").Carbs.Should().Be(30);
        stored.Single(t => t.EventType == "Correction Bolus").Insulin.Should().Be(3);
    }

    [Fact]
    public async Task PostArray_WithAnIdDocumentThenOneWithoutIdentityOfTheSameKey_KeepsOneRow()
    {
        await PostAsync(new object[]
        {
            new { _id = "aaaaaaaaaaaaaaaaaaaaaaaa", eventType = "Carb Correction", created_at = At, carbs = 30 },
            Carbs("Carb Correction", 45),
        });

        var stored = (await StoredAsync()).Should().ContainSingle().Subject;
        stored.Carbs.Should().Be(45);
    }

    [Fact]
    public async Task Post_WithDistinctIdentifiersAtOneTimeAndType_KeepsBoth()
    {
        await PostAsync(new[]
        {
            new { identifier = "11111111-1111-4111-8111-111111111111", eventType = "Carb Correction", created_at = At, carbs = 30 },
            new { identifier = "22222222-2222-4222-8222-222222222222", eventType = "Carb Correction", created_at = At, carbs = 30 },
        });

        (await StoredAsync()).Select(t => t.Carbs).Should().Equal(30d, 30d);
    }

    private static object Carbs(string eventType, double carbs) => new { eventType, created_at = At, carbs };

    private async Task PostAsync(object body)
    {
        var result = await _controller.CreateTreatments(JsonSerializer.SerializeToElement(body));
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    private async Task<List<Treatment>> StoredAsync() =>
        (await _service.GetTreatmentsAsync(count: 100)).ToList();
}
