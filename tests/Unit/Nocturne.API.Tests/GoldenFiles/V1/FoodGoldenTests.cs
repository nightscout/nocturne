using Nocturne.API.Tests.GoldenFiles.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.GoldenFiles.V1;

public class FoodGoldenTests : GoldenFileTestBase
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Fixed timestamps for deterministic tests
    // 2024-03-26T12:00:00Z = 1711454400000
    private const long BaseMillis = 1711454400000;

    public FoodGoldenTests(GoldenFileWebAppFactory factory) : base(factory) { }

    #region Helper Methods

    private static FoodEntity CreateFood(
        int index,
        string name = "Apple",
        string type = "food",
        double carbs = 15,
        double fat = 0.3,
        double protein = 0.5,
        double portion = 100,
        double energy = 52)
    {
        return new FoodEntity
        {
            Id = Guid.Parse($"00000000-0000-0000-0004-{(index + 1):D12}"),
            TenantId = TestTenantId,
            OriginalId = $"eeeeeeeeeeeeeeeeeeeee{(index + 1):D3}",
            Type = type,
            Category = "Fruits",
            Subcategory = "Fresh",
            Name = name,
            Portion = portion,
            Carbs = carbs,
            Fat = fat,
            Protein = protein,
            Energy = energy,
            Gi = GlycemicIndex.Medium,
            Unit = "g",
            SysCreatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            SysUpdatedAt = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
        };
    }

    #endregion

    #region GET /api/v1/food

    [Fact]
    public async Task GetFood_WithEmptyDb_ReturnsEmptyArray()
    {
        var response = await Client.GetAsync("/api/v1/food");
        var captured = await CaptureResponse(response);

        await Verify(captured);
    }

    [Fact]
    public async Task GetFood_WithSeededData_ReturnsArray()
    {
        var foods = new[]
        {
            CreateFood(0, name: "Apple", carbs: 14, fat: 0.2, protein: 0.3, portion: 100, energy: 52),
            CreateFood(1, name: "Banana", carbs: 23, fat: 0.3, protein: 1.1, portion: 120, energy: 89),
            CreateFood(2, name: "Rice", carbs: 28, fat: 0.3, protein: 2.7, portion: 100, energy: 130),
        };
        await SeedFoods(foods);

        var response = await Client.GetAsync("/api/v1/food");
        var captured = await CaptureResponse(response);

        await Verify(captured).ScrubMembers("srvModified", "srvCreated");
    }

    #endregion
}
