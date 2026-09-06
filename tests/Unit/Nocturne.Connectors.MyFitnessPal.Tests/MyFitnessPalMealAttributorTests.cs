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
    private static MfpFoodDiaryEntryNode Item(
        string id, decimal calories, decimal protein, decimal carbs = 0, string? foodId = null) => new()
    {
        Id = id,
        Date = "2025-05-23",
        ConsumedNutrientSet =
            new MfpNutrientSet { Calories = calories, Protein = protein, Carbs = carbs },
        Food = new MfpFood { Id = foodId ?? id, Description = id },
    };

    private static MfpDiaryItem Meal(
        string name, decimal calories, decimal protein, string unit = "calories") => new()
    {
        Type = "diary_meal",
        Date = "2025-05-23",
        DiaryMeal = name,
        NutritionalContents = new MfpDiaryNutrition
        {
            Energy = new MfpDiaryEnergy { Unit = unit, Value = calories },
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
        // Distinguishable entries that fit two different ways round: {a,b} and {c} matches the
        // totals just as well as {c} and {a,b}, so which meal each belongs to is unknowable.
        List<MfpFoodDiaryEntryNode> entries =
            [Item("a", 100, 1), Item("b", 200, 2), Item("c", 300, 3)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 3), Meal("Lunch", 300, 3)];

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

    [Fact]
    public void Attribute_LeavesZeroNutrientEntriesUnnamed_WithoutLosingTheRestOfTheDay()
    {
        // Water or black coffee fits every meal equally. Treating it as solvable would make any
        // day containing one ambiguous; it carries no carbs, so it is simply left unnamed.
        List<MfpFoodDiaryEntryNode> entries =
            [Item("toast", 200, 6), Item("steak", 500, 40), Item("water", 0, 0)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 200, 6), Meal("Dinner", 500, 40)];

        var result = MyFitnessPalMealAttributor.Attribute(entries, meals);

        result["toast"].Should().Be("Breakfast");
        result["steak"].Should().Be("Dinner");
        result.Should().NotContainKey("water");
    }

    [Fact]
    public void Attribute_ResolvesDaysContainingIdenticalEntries()
    {
        // Two servings of the same banana in different meals are two assignment vectors but one
        // outcome, since both publish identical imports, so the day is answerable.
        List<MfpFoodDiaryEntryNode> entries =
        [
            Item("banana-1", 100, 1, carbs: 27, foodId: "banana"),
            Item("toast", 200, 6, carbs: 30),
            Item("banana-2", 100, 1, carbs: 27, foodId: "banana"),
        ];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 7), Meal("Snacks", 100, 1)];

        var result = MyFitnessPalMealAttributor.Attribute(entries, meals);

        result["toast"].Should().Be("Breakfast");
        new[] { result["banana-1"], result["banana-2"] }
            .Should().BeEquivalentTo(["Breakfast", "Snacks"]);
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenEntriesMatchOnEnergyButDifferInCarbs()
    {
        // An apple and a cordial can agree on calories and protein — the two dimensions the
        // search uses — while carrying very different carbs. Treating them as interchangeable
        // would put 25g of carbs at the wrong time of day on a coin flip.
        List<MfpFoodDiaryEntryNode> entries =
        [
            Item("apple", 95, 0.5m, carbs: 25),
            Item("cordial", 95, 0.5m, carbs: 0),
            Item("toast", 200, 6, carbs: 30),
        ];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 295, 6.5m), Meal("Lunch", 95, 0.5m)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_IsStableAcrossRunsForInterchangeableEntries()
    {
        // The same diary must always produce the same answer, or an entry's meal time would
        // flip between syncs.
        List<MfpFoodDiaryEntryNode> entries =
        [
            Item("banana-b", 100, 1, carbs: 27, foodId: "banana"),
            Item("toast", 200, 6, carbs: 30),
            Item("banana-a", 100, 1, carbs: 27, foodId: "banana"),
        ];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 7), Meal("Snacks", 100, 1)];

        var first = MyFitnessPalMealAttributor.Attribute(entries, meals);
        var reordered = MyFitnessPalMealAttributor.Attribute(
            [entries[2], entries[0], entries[1]], meals);

        reordered.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Attribute_ToleratesAMealRowWithNoTotals()
    {
        // An empty meal slot is inert rather than fatal: no solvable entry can be assigned to it.
        List<MfpFoodDiaryEntryNode> entries = [Item("toast", 200, 6, carbs: 30)];
        List<MfpDiaryItem> meals =
        [
            Meal("Breakfast", 200, 6),
            new() { Type = "diary_meal", DiaryMeal = "Snacks", NutritionalContents = null },
        ];

        MyFitnessPalMealAttributor.Attribute(entries, meals)["toast"].Should().Be("Breakfast");
    }

    [Theory]
    [InlineData("kilojoules")]
    [InlineData("kJ")]
    public void Attribute_ConvertsKilojouleUnitVariants(string unit)
    {
        List<MfpFoodDiaryEntryNode> entries = [Item("toast", 200, 6, carbs: 30)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 836.8m, 6, unit: unit)];

        MyFitnessPalMealAttributor.Attribute(entries, meals)["toast"].Should().Be("Breakfast");
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenInterchangeableExceptForServingSize()
    {
        // Same food and nutrients but different serving descriptions: swapping them would swap
        // what each meal reports having been eaten.
        var cup = Item("cup", 100, 1, carbs: 27, foodId: "milk");
        cup.ServingSize = new MfpServingSize { Amount = 1, Unit = "cup" };
        var millilitres = Item("ml", 100, 1, carbs: 27, foodId: "milk");
        millilitres.ServingSize = new MfpServingSize { Amount = 250, Unit = "ml" };

        List<MfpFoodDiaryEntryNode> entries = [cup, millilitres, Item("toast", 200, 6, carbs: 30)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 7), Meal("Snacks", 100, 1)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_TreatsNumericallyEqualNutrientsAsInterchangeable()
    {
        // decimal keeps its scale, so 100 and 100.0 are equal but do not render alike.
        List<MfpFoodDiaryEntryNode> entries =
        [
            Item("banana-1", 100m, 1m, carbs: 27m, foodId: "banana"),
            Item("banana-2", 100.0m, 1.0m, carbs: 27.0m, foodId: "banana"),
            Item("toast", 200, 6, carbs: 30),
        ];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 7), Meal("Snacks", 100, 1)];

        var result = MyFitnessPalMealAttributor.Attribute(entries, meals);

        result["toast"].Should().Be("Breakfast");
        new[] { result["banana-1"], result["banana-2"] }
            .Should().BeEquivalentTo(["Breakfast", "Snacks"]);
    }

    [Fact]
    public void Attribute_CountsOnlySolvableEntriesTowardsTheSizeLimit()
    {
        // A long day of mostly water is still cheap to solve.
        var entries = Enumerable.Range(0, 20).Select(i => Item($"water-{i}", 0, 0)).ToList();
        entries.Add(Item("toast", 200, 6, carbs: 30));
        List<MfpDiaryItem> meals = [Meal("Breakfast", 200, 6)];

        MyFitnessPalMealAttributor.Attribute(entries, meals)["toast"].Should().Be("Breakfast");
    }

    [Fact]
    public void Attribute_ConvertsKilojouleMealTotals()
    {
        // The legacy diary reports energy in the account's unit; the graph always reports calories.
        List<MfpFoodDiaryEntryNode> entries = [Item("toast", 200, 6)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 836.8m, 6, unit: "kilojoules")];

        MyFitnessPalMealAttributor.Attribute(entries, meals)["toast"].Should().Be("Breakfast");
    }

    [Fact]
    public void Attribute_ReturnsNothing_WhenAMealRowHasNoName()
    {
        // Its entries still count towards the day, so they cannot be told apart from the rest.
        List<MfpFoodDiaryEntryNode> entries = [Item("a", 100, 5), Item("b", 200, 8)];
        List<MfpDiaryItem> meals =
        [
            Meal("Breakfast", 100, 5),
            new()
            {
                Type = "diary_meal",
                DiaryMeal = null,
                NutritionalContents = new MfpDiaryNutrition
                {
                    Energy = new MfpDiaryEnergy { Unit = "calories", Value = 200 }, Protein = 8,
                },
            },
        ];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_ReturnsNothing_ForNegativeNutrients()
    {
        // The search prunes on partial sums, which is only sound while contributions cannot shrink.
        List<MfpFoodDiaryEntryNode> entries = [Item("a", -50, 5), Item("b", 150, 5)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 100, 10)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }

    [Fact]
    public void Attribute_ReturnsNothing_ForAPartialDay()
    {
        // An incremental feed would deliver a day a meal at a time; the totals cover the whole day,
        // so a partial set must not be attributed against them.
        List<MfpFoodDiaryEntryNode> entries = [Item("lunch-item", 400, 20)];
        List<MfpDiaryItem> meals = [Meal("Breakfast", 300, 10), Meal("Lunch", 400, 20)];

        MyFitnessPalMealAttributor.Attribute(entries, meals).Should().BeEmpty();
    }
}
