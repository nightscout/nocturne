using System.Net.Http.Json;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Parity.V3;

/// <summary>
/// Parity tests for /api/v3/food endpoints.
/// V3 API follows a generic CRUD pattern with:
/// - SEARCH: GET /food
/// - CREATE: POST /food
/// - READ: GET /food/{id}
/// - UPDATE: PUT /food/{id}
/// - DELETE: DELETE /food/{id}
/// </summary>
public class FoodParityTests : ParityTestBase
{
    public FoodParityTests(ParityTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region Data Seeding

    private async Task SeedFoodV3Async(params object[] foods)
    {
        foreach (var food in foods)
        {
            await NightscoutClient.PostAsJsonAsync("/api/v3/food", food);
            await NocturneClient.PostAsJsonAsync("/api/v3/food", food);
        }
    }

    #endregion

    #region SEARCH - GET /api/v3/food

    [Fact]
    public async Task Search_Empty_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/food");
    }

    [Fact]
    public async Task Search_WithData_ReturnsSameShape()
    {
        var foods = new object[]
        {
            new { type = "food", name = "Apple", category = "Fruit", carbs = 15, portion = 100, unit = "g" },
            new { type = "food", name = "Milk", category = "Dairy", carbs = 12, portion = 250, unit = "ml" },
            new { type = "food", name = "Bread", category = "Grains", carbs = 25, portion = 50, unit = "g" }
        };
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food");
    }

    [Fact]
    public async Task Search_WithLimit_ReturnsSameShape()
    {
        var foods = Enumerable.Range(0, 10)
            .Select(i => (object)new
            {
                type = "food",
                name = $"Food Item {i}",
                category = "Test",
                carbs = 10 + i,
                portion = 100,
                unit = "g"
            })
            .ToArray();
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?limit=5");
    }

    [Fact]
    public async Task Search_WithSkip_ReturnsSameShape()
    {
        var foods = Enumerable.Range(0, 10)
            .Select(i => (object)new
            {
                type = "food",
                name = $"Food Item {i}",
                category = "Test",
                carbs = 10 + i
            })
            .ToArray();
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?skip=3");
    }

    [Fact]
    public async Task Search_WithSort_ReturnsSameShape()
    {
        var foods = new object[]
        {
            new { type = "food", name = "Banana", category = "Fruit", carbs = 20 },
            new { type = "food", name = "Apple", category = "Fruit", carbs = 15 },
            new { type = "food", name = "Cherry", category = "Fruit", carbs = 12 }
        };
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?sort=name");
        await AssertGetParityAsync("/api/v3/food?sort$desc=name");
    }

    [Fact]
    public async Task Search_Filter_Category_ReturnsSameShape()
    {
        var foods = new object[]
        {
            new { type = "food", name = "Apple", category = "Fruit", carbs = 15 },
            new { type = "food", name = "Cheese", category = "Dairy", carbs = 2 },
            new { type = "food", name = "Orange", category = "Fruit", carbs = 12 }
        };
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?category$eq=Fruit");
    }

    [Fact]
    public async Task Search_Filter_Carbs_ReturnsSameShape()
    {
        var foods = new object[]
        {
            new { type = "food", name = "Low Carb", category = "Test", carbs = 5 },
            new { type = "food", name = "Medium Carb", category = "Test", carbs = 20 },
            new { type = "food", name = "High Carb", category = "Test", carbs = 50 }
        };
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?carbs$gte=15");
        await AssertGetParityAsync("/api/v3/food?carbs$lte=25");
    }

    [Fact]
    public async Task Search_Filter_Type_ReturnsSameShape()
    {
        var foods = new object[]
        {
            new { type = "food", name = "Regular Food", category = "Test", carbs = 20 },
            new { type = "quickpick", name = "Quick Snack", category = "Test", carbs = 15 }
        };
        await SeedFoodV3Async(foods);

        await AssertGetParityAsync("/api/v3/food?type$eq=food");
        await AssertGetParityAsync("/api/v3/food?type$eq=quickpick");
    }

    #endregion

    #region CREATE - POST /api/v3/food

    [Fact]
    public async Task Create_Simple_ReturnsSameShape()
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

        await AssertPostParityAsync("/api/v3/food", food);
    }

    [Fact]
    public async Task Create_Complete_ReturnsSameShape()
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
            energy = 750,
            portion = 150,
            unit = "g",
            gi = 2
        };

        await AssertPostParityAsync("/api/v3/food", food);
    }

    [Fact]
    public async Task Create_Quickpick_ReturnsSameShape()
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

        await AssertPostParityAsync("/api/v3/food", food);
    }

    [Fact]
    public async Task Create_WithHidden_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            name = "Hidden Food",
            category = "Test",
            carbs = 10,
            hidden = true
        };

        await AssertPostParityAsync("/api/v3/food", food);
    }

    #endregion

    #region READ - GET /api/v3/food/{id}

    [Fact]
    public async Task Read_NotFound_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/food/nonexistent123456789012");
    }

    #endregion

    #region UPDATE - PUT /api/v3/food/{id}

    [Fact]
    public async Task Update_NotFound_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            name = "Updated Food",
            category = "Test",
            carbs = 30
        };

        await AssertPutParityAsync("/api/v3/food/nonexistent123456789012", food);
    }

    #endregion

    #region DELETE - DELETE /api/v3/food/{id}

    [Fact]
    public async Task Delete_NotFound_ReturnsSameShape()
    {
        await AssertDeleteParityAsync("/api/v3/food/nonexistent123456789012");
    }

    #endregion

    #region Error Cases

    [Fact]
    public async Task Create_Empty_ReturnsSameShape()
    {
        var food = new { };

        await AssertPostParityAsync("/api/v3/food", food);
    }

    [Fact]
    public async Task Create_MissingName_ReturnsSameShape()
    {
        var food = new
        {
            type = "food",
            category = "Test",
            carbs = 20
        };

        await AssertPostParityAsync("/api/v3/food", food);
    }

    [Fact]
    public async Task Search_InvalidLimit_ReturnsSameShape()
    {
        await AssertGetParityAsync("/api/v3/food?limit=-1");
    }

    #endregion
}
