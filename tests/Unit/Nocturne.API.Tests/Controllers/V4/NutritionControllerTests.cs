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
