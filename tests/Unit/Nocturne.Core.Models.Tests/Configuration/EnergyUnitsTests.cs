using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class EnergyUnitsTests
{
    [Theory]
    [InlineData("en-AU")]
    [InlineData("en-NZ")]
    [InlineData("en-ZA")]
    public void A_kilojoule_region_defaults_to_kilojoules(string region)
    {
        EnergyUnits.Resolve(null, region).Should().Be("kJ");
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_region_defaults_to_kilocalories(string? region)
    {
        EnergyUnits.Resolve(null, region).Should().Be("kcal");
    }

    [Theory]
    [InlineData("kcal", "en-AU", "kcal")]
    [InlineData("kJ", "en-GB", "kJ")]
    [InlineData("kJ", null, "kJ")]
    public void An_explicit_unit_wins_over_the_region(string unit, string? region, string expected)
    {
        EnergyUnits.Resolve(unit, region).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_stored_unit_falls_back_to_the_region_default()
    {
        EnergyUnits.Resolve("calories", "en-NZ").Should().Be("kJ");
    }
}
