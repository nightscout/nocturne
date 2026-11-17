using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

public class YearOverviewColorPreferencesTests
{
    private static YearOverviewColorBoundary Pin(double value, double position) => new() { Value = value, Position = position };

    [Fact]
    public void Colors_round_trip_and_reset_without_discarding_other_preferences()
    {
        var preferences = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            YearOverviewColors = new()
            {
                TddBand = [10, 70],
                AvgGlucoseBand = [70, 180],
                AvgGlucoseBoundaries = [Pin(100, 0.3), Pin(200, 0.6)],
            },
        };
        var restored = UserDisplayPreferences.Deserialize(preferences.Serialize());
        restored.YearOverviewColors.Should().BeEquivalentTo(preferences.YearOverviewColors);
        restored.Validate().Should().BeNull();
        restored.ToPresentationOnly().YearOverviewColors.Should().BeEquivalentTo(preferences.YearOverviewColors);
        restored.MergeWith(new() { TimeFormat = "24" });
        restored.YearOverviewColors!.TddBand.Should().Equal(10, 70);
        restored.MergeWith(new() { YearOverviewColors = new() { TddBand = [20, 60] } });
        restored.YearOverviewColors!.AvgGlucoseBoundaries.Should().BeNull();
        restored.MergeWith(new() { YearOverviewColors = new() });
        restored.YearOverviewColors!.TddBand.Should().BeNull();
        restored.GlucoseUnits.Should().Be("mmol");
    }

    [Fact]
    public void Preferences_saved_in_the_retired_shape_load_without_losing_the_rest()
    {
        const string stored = """
            {"glucoseUnits":"mmol","yearOverviewColors":{"advancedMode":true,"avgGlucose":[54,72,180,250],
             "tdd":[10,70],"tddColors":["#000","#fff"],"tddInvert":true,"tddBand":[5,60]}}
            """;

        var restored = UserDisplayPreferences.Deserialize(stored);

        restored.GlucoseUnits.Should().Be("mmol");
        restored.YearOverviewColors!.TddBand.Should().Equal(5, 60);
        restored.Validate().Should().BeNull();
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
        new UserDisplayPreferences { YearOverviewColors = new() { TddBand = values } }.Validate().Should().NotBeNull();

    [Fact]
    public void Tir_and_glucose_windows_are_bounded_but_doses_are_not()
    {
        new YearOverviewColorPreferences { TirBand = [70, 101] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { AvgGlucoseBand = [39, 350] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { AvgGlucoseBand = [40, 351] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { AvgGlucoseBand = [100, 80] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { TirBand = [70, 100], TddBand = [0, 1000], AvgGlucoseBand = [40, 350] }.Validate().Should().BeNull();
    }

    public static TheoryData<YearOverviewColorBoundary[]> InvalidBoundaries => new()
    {
        new[] { Pin(100, 0) },
        new[] { Pin(100, 1) },
        new[] { Pin(40, 0.5) },
        new[] { Pin(350, 0.5) },
        new[] { Pin(double.NaN, 0.5) },
        new[] { Pin(100, 0.5), Pin(100, 0.6) },
        new[] { Pin(100, 0.5), Pin(200, 0.4) },
        new[] { Pin(200, 0.5), Pin(100, 0.6) },
        Enumerable.Range(1, YearOverviewColorPreferences.MaxBoundaries + 1).Select(i => Pin(40 + i, i / 100d)).ToArray(),
    };

    [Theory]
    [MemberData(nameof(InvalidBoundaries))]
    public void Glucose_boundaries_must_increase_in_value_and_position_inside_the_ramp(YearOverviewColorBoundary[] boundaries) =>
        new YearOverviewColorPreferences { AvgGlucoseBoundaries = boundaries }.Validate().Should().NotBeNull();

    [Fact]
    public void Boundaries_follow_each_metrics_limits()
    {
        new YearOverviewColorPreferences { TirBoundaries = [Pin(101, 0.5)] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { TddBoundaries = [Pin(0, 0.5)] }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences
        {
            TirBoundaries = [Pin(70, 0.5)],
            TddBoundaries = [Pin(1000, 0.2)],
            AvgGlucoseBoundaries = [Pin(41, 0.01), Pin(349, 0.99)],
        }.Validate().Should().BeNull();
    }

    [Fact]
    public void Chosen_colors_round_trip()
    {
        var colors = new YearOverviewColorPreferences
        {
            TddLowColor = "#1A2B3C",
            TddHighColor = "#ffffff",
            TddBoundaries = [new() { Value = 30, Position = 0.5, Color = "#ff0000" }, Pin(40, 0.7)],
        };
        var restored = UserDisplayPreferences.Deserialize(new UserDisplayPreferences { YearOverviewColors = colors }.Serialize());
        restored.YearOverviewColors.Should().BeEquivalentTo(colors);
        restored.Validate().Should().BeNull();
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    [InlineData("#1234567")]
    [InlineData("var(--insulin)")]
    [InlineData("")]
    public void A_color_is_six_digit_hex(string color)
    {
        new YearOverviewColorPreferences { TddLowColor = color }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { AvgGlucoseHighColor = color }.Validate().Should().NotBeNull();
        new YearOverviewColorPreferences { CarbsBoundaries = [new() { Value = 5, Position = 0.5, Color = color }] }
            .Validate().Should().NotBeNull();
    }
}
