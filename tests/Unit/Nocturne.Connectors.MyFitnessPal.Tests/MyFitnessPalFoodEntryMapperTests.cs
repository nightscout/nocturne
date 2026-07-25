using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Mappers;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

public class MyFitnessPalFoodEntryMapperTests
{
    private static readonly MyFitnessPalConnectorConfiguration Config = new();

    private static MyFitnessPalFoodEntryMapper Mapper() => new(NullLogger.Instance);

    private static MfpFoodDiaryEntryNode Entry(
        string id = "entry-1",
        string? date = "2026-07-20",
        string? consumedAt = null) => new()
        {
            Id = id,
            Date = date,
            ConsumedAt = consumedAt,
            LoggedAt = "2026-07-20T09:15:00Z",
            Quantity = 2,
            ServingSize = new MfpServingSize { Amount = 1, Unit = "slice", NutritionMultiplier = 1 },
            ConsumedNutrientSet = new MfpNutrientSet { Calories = 180, Protein = 8, Carbs = 30, Fat = 2 },
            Food = new MfpFood
            {
                Id = "food-1",
                Description = "Wholemeal Bread",
                Brand = "Helga",
                NutrientSet = new MfpNutrientSet { Calories = 90, Protein = 4, Carbs = 15, Fat = 1 },
            },
        };

    private static Dictionary<string, string> Breakfast => new() { ["entry-1"] = "Breakfast" };

    private static (DateTimeOffset From, DateTimeOffset To) WholeDay =>
        (new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
         new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Map_TakesNutrientsFromConsumedSet_NotThePerServingSet()
    {
        var (from, to) = WholeDay;

        var import = Mapper().Map([Entry()], Config, from, to, Breakfast)
            .Should().ContainSingle().Subject;

        import.Carbs.Should().Be(30);
        import.Protein.Should().Be(8);
        import.Fat.Should().Be(2);
        import.Energy.Should().Be(180);

        // The food record keeps the per-serving values for deduplication.
        import.Food!.Carbs.Should().Be(15);
        import.Food.Energy.Should().Be(90);
    }

    [Fact]
    public void Map_PopulatesIdentityAndServingFields()
    {
        var (from, to) = WholeDay;

        var import = Mapper().Map([Entry()], Config, from, to, Breakfast)
            .Should().ContainSingle().Subject;

        import.ConnectorSource.Should().Be(DataSources.MyFitnessPalConnector);
        import.ExternalEntryId.Should().Be("entry-1");
        import.ExternalFoodId.Should().Be("food-1");
        import.MealName.Should().Be("Breakfast");
        import.Servings.Should().Be(2);
        import.ServingDescription.Should().Be("2 x 1 slice");
        import.LoggedAt.Should().Be(new DateTimeOffset(2026, 7, 20, 9, 15, 0, TimeSpan.Zero));
        import.Food!.Name.Should().Be("Wholemeal Bread");
        import.Food.BrandName.Should().Be("Helga");
        import.Food.Unit.Should().Be("slice");
    }

    [Fact]
    public void Map_ImportsEntriesTheAttributionCouldNotName()
    {
        var (from, to) = WholeDay;

        // An unattributed day still yields entries; only the meal name and its implied hour are lost.
        var import = Mapper().Map([Entry()], Config, from, to).Should().ContainSingle().Subject;

        import.MealName.Should().BeEmpty();
        import.Carbs.Should().Be(30);
        import.ConsumedAt.Should().Be(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Map_DropsEntriesOutsideTheRequestedWindow()
    {
        var from = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

        Mapper().Map([Entry()], Config, from, to, Breakfast).Should().BeEmpty();
    }

    [Fact]
    public void Map_SkipsEntriesWithAnUnparseableDate()
    {
        var (from, to) = WholeDay;

        Mapper().Map([Entry(date: "not-a-date")], Config, from, to, Breakfast).Should().BeEmpty();
    }

    [Fact]
    public void Map_HandlesEntriesWithoutAFood()
    {
        var (from, to) = WholeDay;
        var entry = Entry();
        entry.Food = null;

        var import = Mapper().Map([entry], Config, from, to, Breakfast)
            .Should().ContainSingle().Subject;

        import.Food.Should().BeNull();
        import.ExternalFoodId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("09:13:00")]
    [InlineData("09:13")]
    public void ResolveConsumedAt_CombinesAReportedTimeOfDayWithTheEntryDate(string consumedAt)
    {
        // Production sends consumedAt as a bare local time. Parsing it as an instant would date the
        // entry to the day of the sync, silently moving years of history onto today.
        var config = new MyFitnessPalConnectorConfiguration { TimezoneOffset = 10 };

        var resolved = MyFitnessPalFoodEntryMapper.ResolveConsumedAt(
            Entry(consumedAt: consumedAt), new DateOnly(2025, 5, 23), null, config);

        // At +10, 09:13 local on the 23rd is 23:13 UTC on the 22nd — matching the loggedAt instant
        // production reports alongside it.
        resolved.ConsumedAt.Should().Be(new DateTimeOffset(2025, 5, 22, 23, 13, 0, TimeSpan.Zero));
        resolved.IsTimeInferred.Should().BeFalse();
    }

    [Fact]
    public void ResolveConsumedAt_PrefersTheReportedTimestamp()
    {
        // Production leaves consumedAt null, but honour it if MyFitnessPal ever populates it.
        var resolved = MyFitnessPalFoodEntryMapper.ResolveConsumedAt(
            Entry(consumedAt: "2026-07-20T08:30:00Z"), new DateOnly(2026, 7, 20), "Dinner", Config);

        resolved.ConsumedAt.Should().Be(new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero));
        resolved.IsTimeInferred.Should().BeFalse();
    }

    [Theory]
    [InlineData("Breakfast", 8)]
    [InlineData("lunch", 12)]
    [InlineData("Dinner", 18)]
    [InlineData("Snacks", 15)]
    public void ResolveConsumedAt_DerivesTheHourFromTheMealName(string mealName, int expectedHour)
    {
        var resolved = MyFitnessPalFoodEntryMapper.ResolveConsumedAt(
            Entry(), new DateOnly(2026, 7, 20), mealName, Config);

        resolved.ConsumedAt.Should().Be(new DateTimeOffset(2026, 7, 20, expectedHour, 0, 0, TimeSpan.Zero));
        resolved.IsTimeInferred.Should().BeTrue();
    }

    [Theory]
    [InlineData("Second Breakfast")]
    [InlineData(null)]
    public void ResolveConsumedAt_FallsBackToMidday_ForARenamedOrUnknownMeal(string? mealName)
    {
        var resolved = MyFitnessPalFoodEntryMapper.ResolveConsumedAt(
            Entry(), new DateOnly(2026, 7, 20), mealName, Config);

        resolved.ConsumedAt.Should().Be(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero));
        resolved.IsTimeInferred.Should().BeTrue();
    }

    [Fact]
    public void FormatServingDescription_OmitsTheMultiplier_ForASingleServing()
    {
        MyFitnessPalFoodEntryMapper
            .FormatServingDescription(new MfpServingSize { Amount = 100, Unit = "g" }, 1)
            .Should().Be("100 g");
    }
}
