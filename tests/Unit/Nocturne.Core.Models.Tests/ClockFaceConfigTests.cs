using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class ClockFaceConfigTests
{
    private static readonly JsonSerializerOptions StorageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void A_face_saved_before_units_existed_reads_as_mgdl_and_12_hour()
    {
        const string stored = """
            {
              "rows": [{ "elements": [{ "type": "sg", "size": 40 }] }],
              "settings": { "bgColor": true, "staleMinutes": 20 }
            }
            """;

        var config = JsonSerializer.Deserialize<ClockFaceConfig>(stored, StorageOptions)!;

        config.Settings.GlucoseUnits.Should().Be("mg/dl");
        config.Settings.TimeFormat.Should().Be("12");
        config.Settings.StaleMinutes.Should().Be(20);
        config.Validate().Should().BeNull();
    }

    [Fact]
    public void A_face_with_no_settings_reads_as_mgdl_and_12_hour()
    {
        var config = JsonSerializer.Deserialize<ClockFaceConfig>("""{ "rows": [] }""", StorageOptions)!;

        config.Settings.GlucoseUnits.Should().Be("mg/dl");
        config.Settings.TimeFormat.Should().Be("12");
    }

    [Fact]
    public void A_stale_per_element_time_format_loads_and_is_dropped_on_the_next_save()
    {
        const string stored = """
            {
              "rows": [{ "elements": [{ "type": "time", "format": "24h" }] }],
              "settings": { "glucoseUnits": "mmol", "timeFormat": "12" }
            }
            """;

        var config = JsonSerializer.Deserialize<ClockFaceConfig>(stored, StorageOptions)!;

        config.Rows.Single().Elements.Single().Type.Should().Be("time");
        config.Settings.TimeFormat.Should().Be("12");
        JsonSerializer.Serialize(config, StorageOptions).Should().NotContain("\"format\"");
    }

    [Theory]
    [InlineData("mmol/L", "24", "settings.glucoseUnits")]
    [InlineData("mmol", "24h", "settings.timeFormat")]
    public void An_invalid_unit_or_time_format_is_refused(string units, string timeFormat, string field)
    {
        var config = new ClockFaceConfig
        {
            Settings = new ClockSettings { GlucoseUnits = units, TimeFormat = timeFormat },
        };

        config.Validate().Should().StartWith(field);
    }

    [Fact]
    public void A_starter_face_takes_its_creators_units_and_time_format()
    {
        var config = ClockFaceConfig.Starter(new UserDisplayPreferences { GlucoseUnits = "mmol", TimeFormat = "24" });

        config.Settings.GlucoseUnits.Should().Be("mmol");
        config.Settings.TimeFormat.Should().Be("24");
        config.Validate().Should().BeNull();
    }

    [Fact]
    public void A_starter_face_for_a_creator_with_no_preferences_is_mgdl_and_12_hour()
    {
        var config = ClockFaceConfig.Starter(new UserDisplayPreferences());

        config.Settings.GlucoseUnits.Should().Be("mg/dl");
        config.Settings.TimeFormat.Should().Be("12");
    }

    [Fact]
    public void A_starter_face_ignores_a_stored_preference_outside_the_allowed_set()
    {
        var config = ClockFaceConfig.Starter(new UserDisplayPreferences { GlucoseUnits = "mmol/L", TimeFormat = "24h" });

        config.Validate().Should().BeNull();
        config.Settings.GlucoseUnits.Should().Be("mg/dl");
        config.Settings.TimeFormat.Should().Be("12");
    }

    [Fact]
    public void A_starter_face_shows_glucose_trend_delta_and_age()
    {
        var rows = ClockFaceConfig.Starter(new UserDisplayPreferences()).Rows
            .Select(r => r.Elements.Select(e => e.Type).ToArray());

        rows.Should().BeEquivalentTo(
            new[] { new[] { "sg", "arrow" }, new[] { "delta" }, new[] { "age" } },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Each_starter_face_is_a_fresh_copy()
    {
        var first = ClockFaceConfig.Starter(new UserDisplayPreferences());
        first.Rows.Clear();
        first.Settings.StaleMinutes = 99;

        var second = ClockFaceConfig.Starter(new UserDisplayPreferences());
        second.Rows.Should().HaveCount(3);
        second.Settings.StaleMinutes.Should().Be(13);
    }

    [Fact]
    public void An_explicit_null_unit_is_refused()
    {
        var config = JsonSerializer.Deserialize<ClockFaceConfig>(
            """{ "settings": { "glucoseUnits": null } }""", StorageOptions)!;

        config.Validate().Should().StartWith("settings.glucoseUnits");
    }
}
