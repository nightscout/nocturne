using FluentAssertions;
using Nocturne.API.Services.Seeding;
using Nocturne.Services.Demo.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Seeding;

/// <summary>The seed table is in kilocalories; the seeder must write the kilojoules food energy is stored in.</summary>
public class SampleDataSeederFoodEnergyTests
{
    [Theory]
    [InlineData("Jelly beans", 418)]
    [InlineData("Apple", 259)]
    [InlineData("Homemade pizza slices", 2176)]
    public void A_seeded_food_is_written_in_kilojoules(string name, double kilojoules)
    {
        var seed = DemoLifestyleSeeds.FoodLibrary.Single(f => f.Name == name);

        var food = SampleDataSeeder.BuildFood(seed, Guid.NewGuid(), position: 1);

        food.Energy.Should().Be(kilojoules);
    }
}
