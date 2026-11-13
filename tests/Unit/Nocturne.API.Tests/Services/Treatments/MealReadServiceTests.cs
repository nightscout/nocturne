using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Exercises <see cref="MealReadService"/> over the real carb intake and bolus repositories on an
/// in-memory SQLite database with directly seeded entities.
/// </summary>
[Trait("Category", "Unit")]
public class MealReadServiceTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();
    private readonly Mock<IDemoModeService> _demoModeMock = new();
    private readonly MealReadService _service;

    public MealReadServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        _dbContext = _db.CreateContext(TestTenantId);
        _dbContext.Tenants.Add(new TenantEntity { Id = TestTenantId, Slug = "test" });
        _dbContext.SaveChanges();

        _demoModeMock.Setup(m => m.IsEnabled).Returns(false);
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TreatmentFood>());

        var contexts = new TestNocturneDbContextFactory(_dbContext);
        var linker = Mock.Of<IDuplicateLinker>();
        var audit = Mock.Of<IAuditContext>();
        _service = new MealReadService(
            new CarbIntakeRepository(contexts, linker, audit, NullLogger<CarbIntakeRepository>.Instance),
            new BolusRepository(contexts, linker, audit, NullLogger<BolusRepository>.Instance),
            _foodServiceMock.Object,
            _demoModeMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    private async Task<MealEvent[]> GetMeals(DateTime from, DateTime to, bool? attributed = null) =>
        (await _service.GetMealsAsync(from, to, attributed)).ToArray();

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

    [Fact]
    public async Task EmptyDatabase_ReturnsEmptyArray()
    {
        var from = DateTime.UtcNow.AddHours(-12);
        var to = DateTime.UtcNow.AddHours(12);

        var body = await GetMeals(from, to);

        body.Should().BeEmpty();
    }

    [Fact]
    public async Task MultipleCarbsAndBolusesSameCorrelation_CollapsesIntoOneEvent()
    {
        var cid = Guid.NewGuid();
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 30.0, cid);
        SeedCarbIntake(now.AddMinutes(-4), 15.0, cid);
        SeedBolus(now.AddMinutes(-5), 2.0, cid);
        SeedBolus(now.AddMinutes(-3), 1.5, cid);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

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
    public async Task OrphanCarbIntakes_EachBecomesSeparateEvent()
    {
        // No CorrelationId on either carb — they must NOT collapse into a single
        // event (the trap of GroupBy(c => c.CorrelationId) collapsing all nulls).
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-10), 20.0, correlationId: null);
        SeedCarbIntake(now.AddMinutes(-5), 30.0, correlationId: null);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(2);
        body.Should().OnlyContain(e => e.Boluses.Length == 0);
        body.Should().OnlyContain(e => e.CarbIntakes.Length == 1);
        body.Sum(e => e.TotalCarbs).Should().Be(50.0);
    }

    [Fact]
    public async Task OrphanCarbsHaveEmptyBoluses()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 25.0, correlationId: null);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(1);
        body[0].Boluses.Should().BeEmpty();
        body[0].TotalInsulin.Should().Be(0.0);
        body[0].CarbIntakes.Should().HaveCount(1);
    }

    [Fact]
    public async Task OrphanCarbHasNullCorrelationId()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 25.0, correlationId: null);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().ContainSingle().Which.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task CorrelatedAndOrphan_EmitsBothEventTypes()
    {
        var now = DateTime.UtcNow;
        var cid = Guid.NewGuid();
        SeedCarbIntake(now.AddMinutes(-10), 30.0, cid);
        SeedBolus(now.AddMinutes(-10), 2.5, cid);
        SeedCarbIntake(now.AddMinutes(-5), 20.0, correlationId: null);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(2);
        body.Should().ContainSingle(e => e.CorrelationId == cid && e.Boluses.Length == 1 && e.TotalCarbs == 30.0);
        body.Should().ContainSingle(e => e.CorrelationId == null && e.Boluses.Length == 0 && e.TotalCarbs == 20.0);
    }

    [Fact]
    public async Task EventTimestamp_IsEarliestInGroup()
    {
        var now = DateTime.UtcNow;
        var cid = Guid.NewGuid();
        var earliestBolusTs = now.AddMinutes(-20);
        var laterCarbTs = now.AddMinutes(-10);
        SeedCarbIntake(laterCarbTs, 40.0, cid);
        SeedBolus(earliestBolusTs, 3.0, cid);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(1);
        body[0].Timestamp.Should().Be(earliestBolusTs);
    }

    [Fact]
    public async Task WithFoodAttribution_PopulatesFoodsAndAttributedCarbs()
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

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(1);
        var evt = body[0];
        evt.Foods.Should().HaveCount(1);
        evt.AttributedCarbs.Should().Be(20.0);
        evt.UnspecifiedCarbs.Should().Be(30.0);
        evt.IsAttributed.Should().BeTrue();
    }

    [Fact]
    public async Task AttributedFilter_OnlyReturnsMatching()
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

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1), attributed: true);

        body.Should().HaveCount(1);
        body[0].CorrelationId.Should().Be(cidAttr);
        body[0].IsAttributed.Should().BeTrue();
    }

    [Fact]
    public async Task ResultsSortedByTimestampDescending()
    {
        var now = DateTime.UtcNow;
        var cidEarly = Guid.NewGuid();
        var cidLate = Guid.NewGuid();
        SeedCarbIntake(now.AddMinutes(-30), 10.0, cidEarly);
        SeedCarbIntake(now.AddMinutes(-5), 10.0, cidLate);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().HaveCount(2);
        body[0].CorrelationId.Should().Be(cidLate);
        body[1].CorrelationId.Should().Be(cidEarly);
    }

    [Fact]
    public async Task OutsideRange_Excluded()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddDays(-5), 40.0);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().BeEmpty();
    }

    [Fact]
    public async Task DemoModeOff_LeavesDemoRecordsOut()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 20.0, dataSource: DataSources.DemoService);
        var real = SeedCarbIntake(now.AddMinutes(-4), 30.0, dataSource: "nocturne-web");

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().ContainSingle().Which.CarbIntakes.Single().Id.Should().Be(real.Id);
    }

    [Fact]
    public async Task DemoModeOn_ReturnsOnlyDemoRecords()
    {
        _demoModeMock.Setup(m => m.IsEnabled).Returns(true);
        var now = DateTime.UtcNow;
        var demo = SeedCarbIntake(now.AddMinutes(-5), 20.0, dataSource: DataSources.DemoService);
        SeedCarbIntake(now.AddMinutes(-4), 30.0, dataSource: "nocturne-web");

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().ContainSingle().Which.CarbIntakes.Single().Id.Should().Be(demo.Id);
    }

    [Fact]
    public async Task IntakeWithNoCarbs_IsNotAMeal()
    {
        var now = DateTime.UtcNow;
        SeedCarbIntake(now.AddMinutes(-5), 0.0);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().BeEmpty();
    }

    [Fact]
    public async Task BolusOfAnotherCorrelation_IsNotAttached()
    {
        var now = DateTime.UtcNow;
        var cid = Guid.NewGuid();
        SeedCarbIntake(now.AddMinutes(-5), 30.0, cid);
        SeedBolus(now.AddMinutes(-5), 2.0, cid);
        SeedBolus(now.AddMinutes(-5), 9.0, Guid.NewGuid());
        SeedBolus(now.AddMinutes(-5), 8.0, correlationId: null);

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1));

        body.Should().ContainSingle().Which.TotalInsulin.Should().Be(2.0);
    }

    [Fact]
    public async Task FoodsAreReadOnceForEveryIntakeInTheWindow()
    {
        var now = DateTime.UtcNow;
        var a = SeedCarbIntake(now.AddMinutes(-10), 30.0, Guid.NewGuid());
        var b = SeedCarbIntake(now.AddMinutes(-5), 20.0);

        await GetMeals(now.AddHours(-1), now.AddHours(1));

        _foodServiceMock.Verify(
            f => f.GetByCarbIntakeIdsAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { a.Id, b.Id }.OrderBy(i => i))),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AttributedFalse_KeepsOnlyUnattributedEvents()
    {
        var now = DateTime.UtcNow;
        var attributed = SeedCarbIntake(now.AddMinutes(-10), 30.0, Guid.NewGuid());
        var plain = SeedCarbIntake(now.AddMinutes(-5), 20.0, Guid.NewGuid());
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TreatmentFood { Id = Guid.NewGuid(), CarbIntakeId = attributed.Id, Carbs = 10m, Portions = 1m } });

        var body = await GetMeals(now.AddHours(-1), now.AddHours(1), attributed: false);

        body.Should().ContainSingle().Which.CarbIntakes.Single().Id.Should().Be(plain.Id);
    }

    [Fact]
    public async Task IntakesExactlyAtTheWindowBounds_AreBothReturned()
    {
        var from = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(4);
        var atFrom = SeedCarbIntake(from, 10.0);
        var atTo = SeedCarbIntake(to, 20.0);
        SeedCarbIntake(from.AddTicks(-1), 30.0);
        SeedCarbIntake(to.AddTicks(1), 40.0);

        var body = await GetMeals(from, to);

        body.SelectMany(e => e.CarbIntakes).Select(c => c.Id)
            .Should().BeEquivalentTo(new[] { atFrom.Id, atTo.Id });
    }
}
