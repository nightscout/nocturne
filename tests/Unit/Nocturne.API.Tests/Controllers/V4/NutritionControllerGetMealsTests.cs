using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Exercises <see cref="NutritionController.GetMeals"/> against an in-memory
/// SQLite database with directly-seeded entities. Verifies that records are
/// grouped by <c>CorrelationId</c> into event-centric <see cref="MealEvent"/>s
/// and that orphan (null-correlation) carb intakes each become their own event.
/// </summary>
[Trait("Category", "Unit")]
public class NutritionControllerGetMealsTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepoMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();
    private readonly Mock<IDemoModeService> _demoModeMock = new();

    public NutritionControllerGetMealsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(options) { TenantId = TestTenantId };
        _dbContext.Database.EnsureCreated();
        _dbContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
        _dbContext.SaveChanges();

        // Default: not demo mode
        _demoModeMock.Setup(m => m.IsEnabled).Returns(false);
        // Default: no food entries
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TreatmentFood>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private NutritionController CreateController()
    {
        var controller = new NutritionController(
            _carbIntakeRepoMock.Object,
            _bolusRepoMock.Object,
            _foodServiceMock.Object,
            _demoModeMock.Object,
            _dbContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private CarbIntakeEntity SeedCarbIntake(
        DateTime timestamp,
        double carbs,
        Guid? correlationId = null,
        string? dataSource = "nocturne-web")
    {
        var entity = new CarbIntakeEntity
        {
            TenantId = TestTenantId,
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            Carbs = carbs,
            CorrelationId = correlationId,
            DataSource = dataSource,
        };
        _dbContext.CarbIntakes.Add(entity);
        _dbContext.SaveChanges();
        return entity;
    }

    private BolusEntity SeedBolus(
        DateTime timestamp,
        double insulin,
        Guid? correlationId = null,
        string? dataSource = "nocturne-web")
    {
        var entity = new BolusEntity
        {
            TenantId = TestTenantId,
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            Insulin = insulin,
            CorrelationId = correlationId,
            DataSource = dataSource,
            BolusKind = "Manual",
        };
        _dbContext.Boluses.Add(entity);
        _dbContext.SaveChanges();
        return entity;
    }

    private static MealEvent[] ExtractBody(ActionResult<MealEvent[]> result)
    {
        var ok = result.Result.Should().BeAssignableTo<OkObjectResult>().Subject;
        return ok.Value.Should().BeAssignableTo<MealEvent[]>().Subject;
    }

    [Fact]
    public async Task GetMeals_EmptyDatabase_ReturnsEmptyArray()
    {
        var controller = CreateController();
        var from = DateTime.UtcNow.AddHours(-12);
        var to = DateTime.UtcNow.AddHours(12);

        var result = await controller.GetMeals(from, to);

        var body = ExtractBody(result);
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMeals_MultipleCarbsAndBolusesSameCorrelation_CollapsesIntoOneEvent()
    {
        var cid = Guid.NewGuid();
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 30.0, cid);
        SeedCarbIntake(now.AddMinutes(-4), 15.0, cid);
        SeedBolus(now.AddMinutes(-5), 2.0, cid);
        SeedBolus(now.AddMinutes(-3), 1.5, cid);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(1);
        var evt = body[0];
        evt.CorrelationId.Should().Be(cid);
        evt.CarbIntakes.Should().HaveCount(2);
        evt.Boluses.Should().HaveCount(2);
        evt.TotalCarbs.Should().Be(45.0);
        evt.TotalInsulin.Should().Be(3.5);
        evt.IsAttributed.Should().BeFalse();
        evt.AttributedCarbs.Should().Be(0.0);
        evt.UnspecifiedCarbs.Should().Be(45.0);
    }

    [Fact]
    public async Task GetMeals_OrphanCarbIntakes_EachBecomesSeparateEvent()
    {
        // No CorrelationId on either carb — they must NOT collapse into a single
        // event (the trap of GroupBy(c => c.CorrelationId) collapsing all nulls).
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-10), 20.0, correlationId: null);
        SeedCarbIntake(now.AddMinutes(-5), 30.0, correlationId: null);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(2);
        body.Should().OnlyContain(e => e.Boluses.Length == 0);
        body.Should().OnlyContain(e => e.CarbIntakes.Length == 1);
        body.Sum(e => e.TotalCarbs).Should().Be(50.0);
    }

    [Fact]
    public async Task GetMeals_OrphanCarbsHaveEmptyBoluses()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 25.0, correlationId: null);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(1);
        body[0].Boluses.Should().BeEmpty();
        body[0].TotalInsulin.Should().Be(0.0);
        body[0].CarbIntakes.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetMeals_CorrelatedAndOrphan_EmitsBothEventTypes()
    {
        var now = DateTime.UtcNow;
        var cid = Guid.NewGuid();
        SeedCarbIntake(now.AddMinutes(-10), 30.0, cid);
        SeedBolus(now.AddMinutes(-10), 2.5, cid);
        SeedCarbIntake(now.AddMinutes(-5), 20.0, correlationId: null);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(2);
        body.Should().ContainSingle(e => e.CorrelationId == cid && e.Boluses.Length == 1 && e.TotalCarbs == 30.0);
        body.Should().ContainSingle(e => e.CorrelationId == Guid.Empty && e.Boluses.Length == 0 && e.TotalCarbs == 20.0);
    }

    [Fact]
    public async Task GetMeals_EventTimestamp_IsEarliestInGroup()
    {
        var now = DateTime.UtcNow;
        var cid = Guid.NewGuid();
        var earliestBolusTs = now.AddMinutes(-20);
        var laterCarbTs = now.AddMinutes(-10);
        SeedCarbIntake(laterCarbTs, 40.0, cid);
        SeedBolus(earliestBolusTs, 3.0, cid);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(1);
        body[0].Timestamp.Should().Be(earliestBolusTs);
    }

    [Fact]
    public async Task GetMeals_WithFoodAttribution_PopulatesFoodsAndAttributedCarbs()
    {
        var cid = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var carb = SeedCarbIntake(now.AddMinutes(-5), 50.0, cid);

        var food = new TreatmentFood
        {
            Id = Guid.NewGuid(),
            CarbIntakeId = carb.Id,
            Carbs = 20.0m,
            Portions = 1m,
        };
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { food });

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(1);
        var evt = body[0];
        evt.Foods.Should().HaveCount(1);
        evt.AttributedCarbs.Should().Be(20.0);
        evt.UnspecifiedCarbs.Should().Be(30.0);
        evt.IsAttributed.Should().BeTrue();
    }

    [Fact]
    public async Task GetMeals_AttributedFilter_OnlyReturnsMatching()
    {
        var now = DateTime.UtcNow;
        var cidAttr = Guid.NewGuid();
        var cidUnattr = Guid.NewGuid();
        var carbAttr = SeedCarbIntake(now.AddMinutes(-10), 30.0, cidAttr);
        SeedCarbIntake(now.AddMinutes(-5), 20.0, cidUnattr);

        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TreatmentFood
                {
                    Id = Guid.NewGuid(),
                    CarbIntakeId = carbAttr.Id,
                    Carbs = 15.0m,
                    Portions = 1m,
                },
            });

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1), attributed: true);

        var body = ExtractBody(result);
        body.Should().HaveCount(1);
        body[0].CorrelationId.Should().Be(cidAttr);
        body[0].IsAttributed.Should().BeTrue();
    }

    [Fact]
    public async Task GetMeals_ResultsSortedByTimestampDescending()
    {
        var now = DateTime.UtcNow;
        var cidEarly = Guid.NewGuid();
        var cidLate = Guid.NewGuid();
        SeedCarbIntake(now.AddMinutes(-30), 10.0, cidEarly);
        SeedCarbIntake(now.AddMinutes(-5), 10.0, cidLate);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().HaveCount(2);
        body[0].CorrelationId.Should().Be(cidLate);
        body[1].CorrelationId.Should().Be(cidEarly);
    }

    [Fact]
    public async Task GetMeals_OutsideRange_Excluded()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddDays(-5), 40.0);

        var controller = CreateController();
        var result = await controller.GetMeals(now.AddHours(-1), now.AddHours(1));

        var body = ExtractBody(result);
        body.Should().BeEmpty();
    }
}
