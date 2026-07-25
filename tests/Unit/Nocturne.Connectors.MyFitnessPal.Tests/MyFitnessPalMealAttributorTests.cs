using FluentAssertions;
using Nocturne.Connectors.MyFitnessPal.Mappers;
using Nocturne.Connectors.MyFitnessPal.Models;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

/// <summary>
///     Production returns itemised entries with no meal, and meal totals with no items, so the
///     assignment has to be recovered by reconciling the two. The first case below is a real day
///     from a live account.
/// </summary>
public class MyFitnessPalMealAttributorTests
{
    private static MfpFoodDiaryEntryNode Item(string id, decimal calories, decimal protein) => new()
    {
        Id = id,
        Date = "2025-05-23",
        ConsumedNutrientSet = new MfpNutrientSet { Calories = calories, Protein = protein },
    };

    private static MfpDiaryItem Meal(string name, decimal calories, decimal protein) => new()
    {
        Type = "diary_meal",
        Date = "2025-05-23",
        DiaryMeal = name,
        NutritionalContents = new MfpDiaryNutrition
        {
            Energy = new MfpDiaryEnergy { Unit = "calories", Value = calories },
            Protein = protein,
        },
    };

    [Fact]
    public void Attribute_RecoversTheMealOfEveryEntry()
    {
        List<MfpFoodDiaryEntryNode> entries =
        [
            Item("muesli-bar", 140, 2),
            Item("cereal-1", 171, 4),
            Item("cereal-2", 171, 4),
            Item("chips", 500, 6),
            Item("pork-chop", 147.6m, 24),
            Item("shapes", 120, 2),
            Item("pear", 140, 1),
            Item("gravy-chips", 500, 8),
            Item("dagwood-dog", 285.17m, 9),
            Item("apple-juice", 100, 0.5m),
            Item("m-and-ms", 105.6m, 1),
        ];

        List<MfpDiaryItem> meals =
        [
            Meal("Breakfast", 482, 10),
            Meal("Lunch", 647.6m, 30),
            Meal("Dinner", 925.17m, 18),
            Meal("Snacks", 325.6m, 3.5m),
        ];

        var result = MyFitnessPalMealAttributor.Attribute(entries, meals);

        result["muesli-bar"].Should().Be("Breakfast");
        result["cereal-1"].Should().Be("Breakfast");
        result["cereal-2"].Should().Be("Breakfast");
        result["chips"].Should().Be("Lunch");
        result["pork-chop"].Should().Be("Lunch");
        result["pear"].Should().Be("Dinner");
        result["gravy-chips"].Should().Be("Dinner");
        result["dagwood-dog"].Should().Be("Dinner");
        result["shapes"].Should().Be("Snacks");
        result["apple-juice"].Should().Be("Snacks");
        result["m-and-ms"].Should().Be("Snacks");
    }

    [Fact]
    public void Attribute_UsesProteinToBreakACalorieTie()
    {
        // Both items have the same calories, so calories alone cannot say which meal they went to.
        List<MfpFoodDiaryEntryNode> entries = [Item("a", 100, 2), Item("b", 100, 9)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 100, 9), Meal("Lunch", 100, 2)];

        var result = MyFitnessPalMealAttributor.Attribute(entries, meals);

        result["a"].Should().Be("Lunch");
        result["b"].Should().Be("Breakfast");
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenTheDayIsAmbiguous()
    {
        // Two interchangeable items across two identical meals: no single answer exists.
        List<MfpFoodDiaryEntryNode> entries = [Item("a", 100, 5), Item("b", 100, 5)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 100, 5), Meal("Lunch", 100, 5)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenTheTwoSourcesDisagree()
    {
        // The meal totals do not account for the logged entries, so there is nothing to solve.
        List<MfpFoodDiaryEntryNode> entries = [Item("a", 100, 5), Item("b", 250, 10)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 100, 5)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_ToleratesIndependentRoundingBetweenTheEndpoints()
    {
        List<MfpFoodDiaryEntryNode> entries = [Item("a", 100.4m, 5.2m)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 100, 5)];

        MyFitnessPalMealAttributor.Attribute(entries, meals)["a"].Should().Be("Breakfast");
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenTheDayHasNoMeals()
    {
        MyFitnessPalMealAttributor.Attribute([Item("a", 100, 5)], []).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_ReturnsNothing_ForADayTooLargeToSolve()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Item($"item-{i}", 10, 1)).ToList();
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 30)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }
}
