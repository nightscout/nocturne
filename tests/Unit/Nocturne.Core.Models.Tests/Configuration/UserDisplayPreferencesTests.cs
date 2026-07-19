using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class UserDisplayPreferencesTests
{
    // ----- Deserialize -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Deserialize_returns_empty_for_null_or_blank(string? json)
    {
        var prefs = UserDisplayPreferences.Deserialize(json);

        prefs.Should().NotBeNull();
        prefs.GlucoseUnits.Should().BeNull();
        prefs.Prediction.Should().BeNull();
        prefs.Chart.Should().BeNull();
    }

    [Fact]
    public void Deserialize_returns_empty_for_malformed_json()
    {
        UserDisplayPreferences.Deserialize("{not valid json").Should().NotBeNull();
    }

    [Fact]
    public void Serialize_then_Deserialize_round_trips_values()
    {
        var original = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            TimeFormat = "24",
            ColorTheme = "trio",
            NightModeSchedule = true,
            SidebarWidget = "graph",
            Prediction = new PredictionPreferences { Enabled = true, Minutes = 45, DisplayMode = "cone" },
            Chart = new ChartPreferences { LineColor = "#123456", AreaOpacity = 0.25, Lookback = 6 },
        };

        var restored = UserDisplayPreferences.Deserialize(original.Serialize());

        restored.GlucoseUnits.Should().Be("mmol");
        restored.TimeFormat.Should().Be("24");
        restored.ColorTheme.Should().Be("trio");
        restored.NightModeSchedule.Should().BeTrue();
        restored.Prediction!.Minutes.Should().Be(45);
        restored.Chart!.LineColor.Should().Be("#123456");
        restored.Chart.AreaOpacity.Should().Be(0.25);
        restored.Chart.Lookback.Should().Be(6);
    }

    // ----- MergeWith -----

    [Fact]
    public void MergeWith_preserves_unset_fields()
    {
        var existing = new UserDisplayPreferences { GlucoseUnits = "mmol", TimeFormat = "24" };
        var incoming = new UserDisplayPreferences { GlucoseUnits = "mg/dl" };

        existing.MergeWith(incoming);

        existing.GlucoseUnits.Should().Be("mg/dl"); // overwritten
        existing.TimeFormat.Should().Be("24"); // preserved (incoming was null)
    }

    [Fact]
    public void MergeWith_merges_nested_prediction_field_by_field()
    {
        var existing = new UserDisplayPreferences
        {
            Prediction = new PredictionPreferences { Enabled = true, Minutes = 30, DisplayMode = "cone" },
        };
        var incoming = new UserDisplayPreferences
        {
            Prediction = new PredictionPreferences { Minutes = 60 },
        };

        existing.MergeWith(incoming);

        existing.Prediction!.Minutes.Should().Be(60); // overwritten
        existing.Prediction.Enabled.Should().BeTrue(); // preserved
        existing.Prediction.DisplayMode.Should().Be("cone"); // preserved
    }

    [Fact]
    public void MergeWith_creates_nested_object_when_absent_on_existing()
    {
        var existing = new UserDisplayPreferences();
        var incoming = new UserDisplayPreferences
        {
            Chart = new ChartPreferences { LineColor = "#abcdef" },
        };

        existing.MergeWith(incoming);

        existing.Chart!.LineColor.Should().Be("#abcdef");
    }

    [Fact]
    public void MergeWith_merges_nested_chart_field_by_field()
    {
        var existing = new UserDisplayPreferences
        {
            Chart = new ChartPreferences { LineColor = "#111111", ShowPoints = true, Lookback = 12 },
        };
        var incoming = new UserDisplayPreferences
        {
            Chart = new ChartPreferences { Lookback = 6 },
        };

        existing.MergeWith(incoming);

        existing.Chart!.Lookback.Should().Be(6); // overwritten
        existing.Chart.LineColor.Should().Be("#111111"); // preserved
        existing.Chart.ShowPoints.Should().BeTrue(); // preserved
    }

    [Fact]
    public void MergeWith_replaces_widget_list_wholesale()
    {
        var existing = new UserDisplayPreferences
        {
            DashboardTopWidgets = new List<WidgetId> { WidgetId.BgDelta, WidgetId.Tdd },
        };
        var incoming = new UserDisplayPreferences
        {
            DashboardTopWidgets = new List<WidgetId> { WidgetId.TirChart },
        };

        existing.MergeWith(incoming);

        existing.DashboardTopWidgets.Should().ContainSingle().Which.Should().Be(WidgetId.TirChart);
    }

    // ----- Validate -----

    [Fact]
    public void Validate_accepts_all_null_fields()
    {
        new UserDisplayPreferences().Validate().Should().BeNull();
    }

    [Fact]
    public void Validate_accepts_valid_values()
    {
        var prefs = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            TimeFormat = "12",
            ColorTheme = "aaps",
            SidebarWidget = "halo-dial",
        };

        prefs.Validate().Should().BeNull();
    }

    [Theory]
    [InlineData("mgdl", null, null, null, "glucoseUnits")]
    [InlineData(null, "48", null, null, "timeFormat")]
    [InlineData(null, null, "midnight", null, "colorTheme")]
    [InlineData(null, null, null, "spinner", "sidebarWidget")]
    public void Validate_rejects_invalid_value(
        string? units, string? timeFormat, string? theme, string? sidebar, string expectedField)
    {
        var prefs = new UserDisplayPreferences
        {
            GlucoseUnits = units,
            TimeFormat = timeFormat,
            ColorTheme = theme,
            SidebarWidget = sidebar,
        };

        prefs.Validate().Should().NotBeNull().And.Contain(expectedField);
    }

    [Fact]
    public void Validate_accepts_valid_nested_values()
    {
        var prefs = new UserDisplayPreferences
        {
            Prediction = new PredictionPreferences { DisplayMode = "uam", Minutes = 30 },
            Chart = new ChartPreferences
            {
                LineColorMode = "continuous",
                PointColorMode = "single",
                AreaMode = "baseline",
                AreaOpacity = 0.5,
                Lookback = 12,
            },
        };

        prefs.Validate().Should().BeNull();
    }

    [Fact]
    public void Validate_rejects_invalid_prediction_display_mode()
    {
        new UserDisplayPreferences { Prediction = new PredictionPreferences { DisplayMode = "hologram" } }
            .Validate().Should().NotBeNull().And.Contain("prediction.displayMode");
    }

    [Fact]
    public void Validate_rejects_invalid_chart_color_mode()
    {
        new UserDisplayPreferences { Chart = new ChartPreferences { LineColorMode = "rainbow" } }
            .Validate().Should().NotBeNull().And.Contain("chart.lineColorMode");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Validate_rejects_area_opacity_out_of_range(double opacity)
    {
        new UserDisplayPreferences { Chart = new ChartPreferences { AreaOpacity = opacity } }
            .Validate().Should().NotBeNull().And.Contain("areaOpacity");
    }

    [Fact]
    public void Validate_rejects_negative_prediction_minutes()
    {
        new UserDisplayPreferences { Prediction = new PredictionPreferences { Minutes = -5 } }
            .Validate().Should().NotBeNull().And.Contain("prediction.minutes");
    }

    [Fact]
    public void Validate_rejects_non_positive_lookback()
    {
        new UserDisplayPreferences { Chart = new ChartPreferences { Lookback = 0 } }
            .Validate().Should().NotBeNull().And.Contain("chart.lookback");
    }
}
