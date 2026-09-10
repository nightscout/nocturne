using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

public class YearOverviewColorPreferencesTests
{
    [Fact]
    public void Colors_round_trip_and_reset_without_discarding_other_preferences()
    {
        var preferences = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            YearOverviewColors = new() { Tdd = [10, 70], AvgGlucose = [54, 70, 180, 250] },
        };
        var restored = UserDisplayPreferences.Deserialize(preferences.Serialize());
        restored.YearOverviewColors.Should().BeEquivalentTo(preferences.YearOverviewColors);
        restored.Validate().Should().BeNull();
        restored.ToPresentationOnly().YearOverviewColors.Should().BeEquivalentTo(preferences.YearOverviewColors);
        restored.MergeWith(new() { TimeFormat = "24" });
        restored.YearOverviewColors!.Tdd.Should().Equal(10, 70);
        restored.MergeWith(new() { YearOverviewColors = new() { Tdd = [20, 60] } });
        restored.YearOverviewColors!.AvgGlucose.Should().BeNull();
        restored.MergeWith(new() { YearOverviewColors = new() });
        restored.YearOverviewColors!.Tdd.Should().BeNull();
        restored.GlucoseUnits.Should().Be("mmol");
    }

    public static TheoryData<double[]> InvalidRanges => new()
    {
        Array.Empty<double>(), new double[] { 10 }, new double[] { 1, 2, 3 },
        new double[] { -1, 70 }, new double[] { 10, 10 }, new double[] { 70, 10 },
        new double[] { double.NaN, 70 }, new double[] { 0, double.PositiveInfinity },
    };

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public void Invalid_ranges_are_rejected(double[] values) =>
        new UserDisplayPreferences { YearOverviewColors = new() { Tdd = values } }.Validate().Should().NotBeNull();

    [Theory]
    [InlineData(40, 70, 180, 250)]
    [InlineData(54, 70, 180, 350)]
    [InlineData(54, 70, 70, 250)]
    [InlineData(54, 180, 70, 250)]
    public void Glucose_boundaries_must_increase_within_the_palette(double a, double b, double c, double d) =>
        new YearOverviewColorPreferences { AvgGlucose = [a, b, c, d] }.Validate().Should().NotBeNull();

    [Fact]
    public void Tir_is_bounded_but_doses_are_not()
    {
        new YearOverviewColorPreferences { Tir = [70, 101] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { Tir = [70, 100], Tdd = [0, 1000], AvgGlucose = [41, 42, 43, 44] }.Validate().Should().BeNull();
    }
}
