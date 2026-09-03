using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Exercises <see cref="NutritionController.CreateMeal"/> against real V4 repositories
/// wired to an in-memory SQLite database. We need the real repositories (not mocks)
/// because the test contract depends on idempotent upsert by (DataSource,
/// SyncIdentifier) and on shared-transaction semantics.
/// </summary>
[Trait("Category", "Unit")]
public class NutritionControllerMealsTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly NocturneDbContext _dbContext;
    private readonly BolusRepository _bolusRepo;
    private readonly CarbIntakeRepository _carbIntakeRepo;
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();
    private readonly Mock<IDemoModeService> _demoModeMock = new();
    private readonly RecordingV4RecordBroadcaster<Bolus> _bolusBroadcaster = new();
    private readonly RecordingV4RecordBroadcaster<CarbIntake> _carbBroadcaster = new();

    public NutritionControllerMealsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_options) { TenantId = TestTenantId };
        _dbContext.Database.EnsureCreated();
        _dbContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
        _dbContext.SaveChanges();

        var dedupMock = new Mock<IDeduplicationService>();
        var auditMock = new Mock<IAuditContext>().Object;
        var ctxFactory = new TestTenantDbContextFactory(_dbContext);
        _bolusRepo = new BolusRepository(
            ctxFactory,
            dedupMock.Object,
            auditMock,
            NullLogger<BolusRepository>.Instance,
            _bolusBroadcaster);
        _carbIntakeRepo = new CarbIntakeRepository(
            ctxFactory,
            dedupMock.Object,
            auditMock,
            NullLogger<CarbIntakeRepository>.Instance,
            _carbBroadcaster);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private NutritionController CreateController()
    {
        var controller = new NutritionController(
            _carbIntakeRepo,
            _bolusRepo,
            _foodServiceMock.Object,
            _demoModeMock.Object,
            _dbContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static CreateMealResponse ExtractBody(ActionResult<CreateMealResponse> result)
    {
        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return objectResult.Value.Should().BeOfType<CreateMealResponse>().Subject;
    }

    private static int ExtractStatus(ActionResult<CreateMealResponse> result)
    {
        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return objectResult.StatusCode.GetValueOrDefault();
    }

    [Fact]
    public async Task CreateMeal_NewRecord_Returns201WithSharedCorrelationId()
    {
        var controller = CreateController();
        var request = new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.5,
            Carbs = 45.0,
        };

        var result = await controller.CreateMeal(request, default);

        ExtractStatus(result).Should().Be(StatusCodes.Status201Created);
        var body = ExtractBody(result);
        body.CorrelationId.Should().NotBeEmpty();
        body.Bolus.CorrelationId.Should().Be(body.CorrelationId);
        body.CarbIntake.CorrelationId.Should().Be(body.CorrelationId);
    }

    [Fact]
    public async Task CreateMeal_WithoutSuppliedCorrelationId_ServerMintsSingleIdForBoth()
    {
        // Guard against the bug where each record defaults to its own GUID.
        var controller = CreateController();
        var request = new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            Carbs = 30.0,
        };

        var result = await controller.CreateMeal(request, default);

        var body = ExtractBody(result);
        body.Bolus.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        body.CarbIntake.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        body.Bolus.CorrelationId.Should().Be(body.CarbIntake.CorrelationId);
    }

    [Fact]
    public async Task CreateMeal_WithSuppliedCorrelationId_Propagates()
    {
        var controller = CreateController();
        var cid = Guid.NewGuid();
        var request = new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 4.0,
            Carbs = 25.0,
            CorrelationId = cid,
        };

        var result = await controller.CreateMeal(request, default);

        var body = ExtractBody(result);
        body.CorrelationId.Should().Be(cid);
        body.Bolus.CorrelationId.Should().Be(cid);
        body.CarbIntake.CorrelationId.Should().Be(cid);
    }

    [Fact]
    public async Task CreateMeal_FullRetryWithSameSyncIdentifier_Returns200()
    {
        var controller = CreateController();
        var request = new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.5,
            Carbs = 45.0,
            DataSource = "aaps",
            SyncIdentifier = Guid.NewGuid().ToString(),
        };

        var first = await controller.CreateMeal(request, default);
        ExtractStatus(first).Should().Be(StatusCodes.Status201Created);

        var second = await controller.CreateMeal(request, default);
        ExtractStatus(second).Should().Be(StatusCodes.Status200OK);

        // Only one of each row should exist after the retry.
        var bolusCount = await _dbContext.Boluses.CountAsync();
        var carbCount = await _dbContext.CarbIntakes.CountAsync();
        bolusCount.Should().Be(1);
        carbCount.Should().Be(1);
    }

    [Fact]
    public async Task CreateMeal_PartialIdempotentHit_PreservesExistingBolusCorrelationId()
    {
        var controller = CreateController();
        var existingCid = Guid.NewGuid();
        var syncId = Guid.NewGuid().ToString();

        // Arrange: a bolus already exists with (DataSource, SyncIdentifier) and
        // its own CorrelationId (simulating a prior POST /insulin/boluses call).
        await _bolusRepo.CreateAsync(new Bolus
        {
            Timestamp = DateTime.UtcNow,
            DataSource = "aaps",
            SyncIdentifier = syncId,
            Insulin = 5.5,
            Kind = BolusKind.Manual,
            CorrelationId = existingCid,
        }, WriteOrigin.Live);

        // Act: a meal is posted with the same (DataSource, SyncIdentifier) but a
        // different supplied CorrelationId. The existing bolus's CorrelationId
        // must win and the newly-created carb must be stamped with it.
        var newCid = Guid.NewGuid();
        var request = new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.5,
            Carbs = 45.0,
            DataSource = "aaps",
            SyncIdentifier = syncId,
            CorrelationId = newCid,
        };

        var result = await controller.CreateMeal(request, default);

        var body = ExtractBody(result);
        body.Bolus.CorrelationId.Should().Be(existingCid);
        body.CarbIntake.CorrelationId.Should().Be(existingCid);
        body.CorrelationId.Should().Be(existingCid);

        // The bolus didn't get a new row (idempotent hit), but the carb did.
        (await _dbContext.Boluses.CountAsync()).Should().Be(1);
        (await _dbContext.CarbIntakes.CountAsync()).Should().Be(1);
        // Overall status should be 201 because the carb half was new.
        ExtractStatus(result).Should().Be(StatusCodes.Status201Created);
    }

    /// <remarks>
    /// The two halves are written on separate contexts, so the refusal has to be known before the
    /// bolus write commits. Pins that the preflight, not the second write, is what refuses.
    /// </remarks>
    [Fact]
    public async Task CreateMeal_WhenAUserDeletedCarbIntakeHoldsTheKey_RefusesWithoutWritingTheBolus()
    {
        const string dataSource = "aaps";
        const string syncIdentifier = "meal-sync-1";
        var liveBolusId = SeedLiveBolus(dataSource, syncIdentifier, insulin: 5.5);
        SeedUserDeletedCarbIntake(dataSource, syncIdentifier, carbs: 45.0);

        var act = () => CreateController().CreateMeal(new CreateMealRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 9.9,
            Carbs = 99.0,
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
        }, default);

        await act.Should().ThrowAsync<RecreationBlockedException>()
            .WithMessage($"*{syncIdentifier}*");

        await using var verify = new NocturneDbContext(_options) { TenantId = TestTenantId };
        var bolus = await verify.Boluses.AsNoTracking().SingleAsync();
        bolus.Id.Should().Be(liveBolusId);
        bolus.Insulin.Should().Be(5.5, "the bolus half must not have been upserted behind the refusal");
        (await verify.CarbIntakes.CountAsync()).Should().Be(0, "the carb intake the user deleted stays deleted");

        _bolusBroadcaster.Created.Should().BeEmpty();
        _bolusBroadcaster.Updated.Should().BeEmpty("a refused meal broadcasts nothing");
        _carbBroadcaster.Created.Should().BeEmpty();
        _carbBroadcaster.Updated.Should().BeEmpty();
    }

    private Guid SeedLiveBolus(string dataSource, string syncIdentifier, double insulin)
    {
        var entity = new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = DateTime.UtcNow,
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
            Insulin = insulin,
        };
        using var ctx = new NocturneDbContext(_options) { TenantId = TestTenantId };
        ctx.Boluses.Add(entity);
        ctx.SaveChanges();
        return entity.Id;
    }

    private void SeedUserDeletedCarbIntake(string dataSource, string syncIdentifier, double carbs)
    {
        var entity = new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = DateTime.UtcNow,
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
            Carbs = carbs,
            DeletedAt = DateTime.UtcNow,
        };
        using var ctx = new NocturneDbContext(_options) { TenantId = TestTenantId };
        var entry = ctx.CarbIntakes.Add(entity);
        entry.Property("DeletedByUser").CurrentValue = true;
        ctx.SaveChanges();
    }

    [Fact]
    public async Task CreateMeal_DefaultTimestamp_Returns400()
    {
        var controller = CreateController();
        var request = new CreateMealRequest
        {
            // Timestamp omitted
            Insulin = 5.0,
            Carbs = 30.0,
        };

        var result = await controller.CreateMeal(request, default);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
    }
}
