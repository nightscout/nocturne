using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class NutritionControllerTests
{
    private readonly Mock<ICarbIntakeRepository> _repoMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ITreatmentFoodService> _foodServiceMock = new();
    private readonly Mock<IFoodRepository> _foodRepoMock = new();
    private readonly Mock<IMealReadService> _mealReadMock = new();

    private NutritionController CreateController()
    {
        var controller = new NutritionController(
            _repoMock.Object,
            _bolusRepoMock.Object,
            _foodServiceMock.Object,
            _foodRepoMock.Object,
            _mealReadMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private void SetupCreatePassthrough(Action<CarbIntake> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<CarbIntake, WriteOrigin, CancellationToken>((c, _, _) => onCreate(c))
            .ReturnsAsync((CarbIntake c, WriteOrigin origin, CancellationToken _) => c);
    }

    [Fact]
    public async Task CreateCarbIntake_PassesThroughCorrelationId()
    {
        var cid = Guid.NewGuid();
        CarbIntake? captured = null;
        SetupCreatePassthrough(c => captured = c);

        var controller = CreateController();
        var request = new CreateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 40,
            CorrelationId = cid,
        };

        await controller.CreateCarbIntake(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(cid);
    }

    [Fact]
    public async Task CreateCarbIntake_WithoutCorrelationId_ServerMintsNonEmptyGuid()
    {
        CarbIntake? captured = null;
        SetupCreatePassthrough(c => captured = c);

        var controller = CreateController();
        var request = new CreateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 40,
            // CorrelationId intentionally omitted
        };

        await controller.CreateCarbIntake(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UpdateCarbIntake_RequestCorrelationIdWins_WhenSupplied()
    {
        var existingCid = Guid.NewGuid();
        var requestCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new CarbIntake
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
            CorrelationId = existingCid,
        };
        CarbIntake? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CarbIntake, WriteOrigin, CancellationToken>((_, c, _, _) => captured = c)
            .ReturnsAsync((Guid _, CarbIntake c, WriteOrigin origin, CancellationToken _) => c);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
            CorrelationId = requestCid,
        };

        await controller.UpdateCarbIntake(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(requestCid);
    }

    [Fact]
    public async Task UpdateCarbIntake_PreservesExistingCorrelationId_WhenRequestOmits()
    {
        var existingCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new CarbIntake
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
            CorrelationId = existingCid,
        };
        CarbIntake? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<CarbIntake>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CarbIntake, WriteOrigin, CancellationToken>((_, c, _, _) => captured = c)
            .ReturnsAsync((Guid _, CarbIntake c, WriteOrigin origin, CancellationToken _) => c);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
            // CorrelationId intentionally omitted
        };

        await controller.UpdateCarbIntake(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(existingCid);
    }

    [Fact]
    public async Task UpdateCarbIntake_ReturnsNotFound_WhenExistingMissing()
    {
        var id = Guid.NewGuid();
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CarbIntake?)null);

        var controller = CreateController();
        var request = new UpdateCarbIntakeRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Carbs = 30,
        };

        var result = await controller.UpdateCarbIntake(id, request);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetCarbIntakes_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        await CreateController().GetCarbIntakes(null, null, V4ReadLimits.MaxPageSize, 0);

        VerifyCarbIntakesFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetCarbIntakes_LimitAboveCeiling_IsClamped()
    {
        await CreateController().GetCarbIntakes(null, null, V4ReadLimits.MaxPageSize + 1, -1);

        VerifyCarbIntakesFetched(V4ReadLimits.MaxPageSize, 0);
    }

    [Fact]
    public async Task GetCarbIntakes_FullPageWithoutIncludeTotal_LeavesTotalNullWithoutCounting()
    {
        _repoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CarbIntake()]);

        var result = await CreateController().GetCarbIntakes(null, null, 1, 0);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PaginatedResponse<CarbIntake>>().Subject;
        response.Pagination.Total.Should().BeNull();
        _repoMock.Verify(r => r.CountAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void VerifyCarbIntakesFetched(int limit, int offset) =>
        _repoMock.Verify(r => r.GetAsync(
            null, null, null, null, limit, offset, true, null, null, It.IsAny<CancellationToken>()), Times.Once);

    [Fact]
    public async Task GetMeals_PassesTheWindowAndFilterToTheService_AndReturnsItsEvents()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var events = new[] { new MealEvent { CorrelationId = Guid.NewGuid() } };
        _mealReadMock
            .Setup(m => m.GetMealsAsync(from, to, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        var result = await CreateController().GetMeals(from, to, true);

        ((OkObjectResult)result.Result!).Value.Should().BeEquivalentTo(events);
    }

    [Fact]
    public async Task GetMeals_WithoutAWindow_ReadsTodayUtc()
    {
        _mealReadMock
            .Setup(m => m.GetMealsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await CreateController().GetMeals();

        _mealReadMock.Verify(m => m.GetMealsAsync(
            DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(1), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddCarbIntakeFood_UnknownCarbIntake_ReturnsNotFoundWithoutWriting()
    {
        _repoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CarbIntake?)null);

        var result = await CreateController().AddCarbIntakeFood(
            Guid.NewGuid(), new CarbIntakeFoodRequest { Carbs = 10m });

        result.Result.Should().BeOfType<NotFoundResult>();
        _foodServiceMock.Verify(f => f.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCarbIntakeFood_UnknownCatalogFood_ReturnsBadRequestWithoutWriting()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        _foodRepoMock
            .Setup(f => f.GetCatalogFoodAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogFoodPortion?)null);

        var result = await CreateController().AddCarbIntakeFood(
            carbId, new CarbIntakeFoodRequest { FoodId = "missing", Portions = 1m });

        result.Result.Should().BeOfType<BadRequestResult>();
        _foodServiceMock.Verify(f => f.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddCarbIntakeFood_CatalogFoodByPortions_StoresCarbsFromThePortionSize()
    {
        var carbId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        _foodRepoMock
            .Setup(f => f.GetCatalogFoodAsync(foodId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogFoodPortion(foodId, 12.5));
        TreatmentFood? added = null;
        _foodServiceMock
            .Setup(f => f.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentFood, CancellationToken>((e, _) => added = e)
            .ReturnsAsync((TreatmentFood e, CancellationToken _) => e);

        await CreateController().AddCarbIntakeFood(
            carbId, new CarbIntakeFoodRequest { FoodId = foodId.ToString(), Portions = 2m });

        added.Should().NotBeNull();
        added!.FoodId.Should().Be(foodId);
        added.Portions.Should().Be(2m);
        added.Carbs.Should().Be(25.0m);
    }

    [Fact]
    public async Task AddCarbIntakeFood_CarbsOnlyEntryWithoutAFood_NeedsNoCatalogLookup()
    {
        var carbId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(carbId, It.IsAny<CancellationToken>())).ReturnsAsync(new CarbIntake { Id = carbId });
        TreatmentFood? added = null;
        _foodServiceMock
            .Setup(f => f.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentFood, CancellationToken>((e, _) => added = e)
            .ReturnsAsync((TreatmentFood e, CancellationToken _) => e);

        await CreateController().AddCarbIntakeFood(carbId, new CarbIntakeFoodRequest { Carbs = 15m });

        added!.FoodId.Should().BeNull();
        added.Carbs.Should().Be(15m);
        _foodRepoMock.Verify(f => f.GetCatalogFoodAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateCarbIntakeFood_CarbsChangeOnACatalogFood_RecomputesPortionsFromTheExistingFood()
    {
        var carbId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var existing = new TreatmentFood { Id = entryId, CarbIntakeId = carbId, FoodId = foodId, Portions = 1m, Carbs = 10m };
        _foodServiceMock
            .Setup(f => f.GetByCarbIntakeIdAsync(carbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TreatmentFoodBreakdown { CarbIntakeId = carbId, Foods = [existing] });
        _foodRepoMock
            .Setup(f => f.GetCatalogFoodAsync(foodId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogFoodPortion(foodId, 10.0));
        TreatmentFood? updated = null;
        _foodServiceMock
            .Setup(f => f.UpdateAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()))
            .Callback<TreatmentFood, CancellationToken>((e, _) => updated = e)
            .ReturnsAsync((TreatmentFood e, CancellationToken _) => e);

        await CreateController().UpdateCarbIntakeFood(
            carbId, entryId, new CarbIntakeFoodRequest { Carbs = 25m, InputMode = CarbIntakeFoodInputMode.Carbs });

        updated!.Id.Should().Be(entryId);
        updated.Portions.Should().Be(2.5m);
        updated.Carbs.Should().Be(25m);
    }
}
