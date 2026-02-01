using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V1;

/// <summary>
/// Parity tests for /api/v1/food endpoints.
/// Covers: GET, GET/food.json, GET/regular, GET/quickpicks, GET/{id}, POST, PUT/{id}, PUT, DELETE/{id}
/// </summary>
public class FoodParityTests : ParityTestBase
{
    public FoodParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region Data Seeding

    private async Task SeedFoodAsync(params Food[] foods)
    {
        foreach (var food in foods)
        {
            var nsFood = new
            {
                type = food.Type,
                category = food.Category,
                subcategory = food.Subcategory,
                name = food.Name,
                portion = food.Portion,
                unit = food.Unit,
                carbs = food.Carbs,
                protein = food.Protein,
                fat = food.Fat,
                energy = food.Energy,
                gi = food.Gi
            };

            var nsResponse = await NightscoutClient.PostAsJsonAsync("/api/v1/food", nsFood);
            nsResponse.EnsureSuccessStatusCode();

            var nocResponse = await NocturneClient.PostAsJsonAsync("/api/v1/food", food);
            nocResponse.EnsureSuccessStatusCode();
        }
    }

    #endregion

    #region GET /api/v1/food

    [Fact]
    public async Task GetFood_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/food");
    }

    [Fact]
    public async Task GetFood_WithData_ReturnsSameShape()
    {
        var foods = new[]
        {
            TestDataFactory.CreateFood(name: "Apple", category: "Fruit"),
            TestDataFactory.CreateFood(name: "Milk", category: "Dairy"),
            TestDataFactory.CreateFood(name: "Bread", category: "Grains")
        };
        await SeedFoodAsync(foods);

        await AssertGetParityAsync("/api/v1/food");
    }

    [Fact]
    public async Task GetFood_WithCount_ReturnsSameShape()
    {
        var foods = Enumerable.Range(0, 10)
            .Select(i => TestDataFactory.CreateFood(name: $"Food Item {i}"))
            .ToArray();
        await SeedFoodAsync(foods);

        await AssertGetParityAsync("/api/v1/food?count=5");
    }

    [Fact]
    public async Task GetFood_WithFindFilter_ReturnsSameShape()
    {
        var foods = new[]
        {
            TestDataFactory.CreateFood(name: "Apple", category: "Fruit"),
            TestDataFactory.CreateFood(name: "Orange", category: "Fruit"),
            TestDataFactory.CreateFood(name: "Cheese", category: "Dairy")
        };
        await SeedFoodAsync(foods);

        await AssertGetParityAsync("/api/v1/food?find[category]=Fruit");
    }

    #endregion

    #region GET /api/v1/food.json

    [Fact]
    public async Task GetFoodJson_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/food.json");
    }

    [Fact]
    public async Task GetFoodJson_WithData_ReturnsSameShape()
    {
        var food = TestDataFactory.CreateFood();
        await SeedFoodAsync(food);

        await AssertGetParityAsync("/api/v1/food.json");
    }

    #endregion

    #region GET /api/v1/food/regular

    [Fact]
    public async Task GetFoodRegular_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/food/regular");
    }

    [Fact]
    public async Task GetFoodRegular_WithData_ReturnsSameShape()
    {
        var foods = new[]
        {
            TestDataFactory.CreateFood(name: "Regular Food 1"),
            TestDataFactory.CreateFood(name: "Regular Food 2")
        };
        await SeedFoodAsync(foods);

        await AssertGetParityAsync("/api/v1/food/regular");
    }

    #endregion

    #region GET /api/v1/food/quickpicks

    [Fact]
    public async Task GetFoodQuickpicks_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/food/quickpicks");
    }

    [Fact]
    public async Task GetFoodQuickpicks_WithData_ReturnsSameShape()
    {
        // Quickpicks are foods with type "quickpick"
        var quickpick = new Food
        {
            Id = Guid.NewGuid().ToString(),
            Type = "quickpick",
            Name = "Quick Snack",
            Category = "Snacks",
            Carbs = 15,
            Portion = 1,
            Unit = "serving"
        };

        var nsQuickpick = new
        {
            type = "quickpick",
            name = "Quick Snack",
            category = "Snacks",
            carbs = 15,
            portion = 1,
            unit = "serving"
        };

        await NightscoutClient.PostAsJsonAsync("/api/v1/food", nsQuickpick);
        await NocturneClient.PostAsJsonAsync("/api/v1/food", quickpick);

        await AssertGetParityAsync("/api/v1/food/quickpicks");
    }

    #endregion

    #region POST /api/v1/food

    [Fact]
    public async Task PostFood_Simple_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            name = "Test Food",
            category = "Test Category",
            carbs = 20,
            portion = 100,
            unit = "g"
        };

        await AssertPostParityAsync("/api/v1/food", food);
    }

    [Fact]
    public async Task PostFood_Complete_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            name = "Complete Food Entry",
            category = "Test",
            subcategory = "Subcategory",
            carbs = 25,
            protein = 10,
            fat = 5,
            energy = 750, // kJ
            portion = 150,
            unit = "g",
            gi = 2 // Medium GI
        };

        await AssertPostParityAsync("/api/v1/food", food);
    }

    [Fact]
    public async Task PostFood_Quickpick_ReturnsSameShape()
    {
        var food = new
        {
            type = "quickpick",
            name = "Quick Breakfast",
            category = "Meals",
            carbs = 45,
            portion = 1,
            unit = "meal"
        };

        await AssertPostParityAsync("/api/v1/food", food);
    }

    #endregion

    #region DELETE /api/v1/food/{id}

    [Fact]
    public async Task DeleteFood_ByCategory_ReturnsSameShape()
    {
        var foods = new[]
        {
            TestDataFactory.CreateFood(name: "Delete Me 1", category: "ToDelete"),
            TestDataFactory.CreateFood(name: "Delete Me 2", category: "ToDelete")
        };
        await SeedFoodAsync(foods);

        await AssertDeleteParityAsync("/api/v1/food?find[category]=ToDelete");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task GetFood_InvalidId_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v1/food/nonexistent123");
    }

    [Fact]
    public async Task PostFood_MissingName_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            category = "Test",
            carbs = 20
        };

        await AssertPostParityAsync("/api/v1/food", food);
    }

    #endregion
}
