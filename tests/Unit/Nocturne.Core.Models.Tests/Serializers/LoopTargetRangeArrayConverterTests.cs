using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;
using Xunit;

namespace Nocturne.Core.Models.Tests.Serializers;

[Trait("Category", "Unit")]
public class LoopTargetRangeArrayConverterTests
{
    private static readonly JsonSerializerOptions Legacy = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LoopTargetRangeArrayConverter.ServeAsArrays } },
    };

    private static LoopOverridePreset Preset(string targetRange) =>
        JsonSerializer.Deserialize<LoopOverridePreset>($$"""{"targetRange":{{targetRange}}}""")!;

    [Theory]
    [InlineData("[100,110]", 100, 110)]
    [InlineData("""["100","110"]""", 100, 110)]
    [InlineData("""{"minValue":100,"maxValue":110}""", 100, 110)]
    public void A_range_with_both_bounds_is_read_in_either_form(string json, double min, double max)
    {
        var range = Preset(json).TargetRange;

        range!.MinValue.Should().Be(min);
        range.MaxValue.Should().Be(max);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[100]")]
    [InlineData("[100,110,120]")]
    [InlineData("""[100,"high"]""")]
    [InlineData("[100,null]")]
    [InlineData("[[100],110]")]
    [InlineData("[100,{\"a\":1}]")]
    public void An_array_that_is_not_two_numbers_reads_as_no_range(string json)
    {
        Preset(json).TargetRange.Should().BeNull();
    }

    [Fact]
    public void An_array_read_leaves_the_reader_past_the_array()
    {
        var preset = JsonSerializer.Deserialize<LoopOverridePreset>("""{"targetRange":[1],"name":"After"}""")!;

        preset.Name.Should().Be("After");
    }

    [Theory]
    [InlineData(100.0, 110.0, "[100,110]")]
    [InlineData(100.0, null, "null")]
    [InlineData(null, 110.0, "null")]
    [InlineData(null, null, "null")]
    public void The_legacy_form_writes_both_bounds_as_an_array_and_a_partial_range_as_null(double? min, double? max, string expected)
    {
        var preset = new LoopOverridePreset { TargetRange = new LoopTargetRange { MinValue = min, MaxValue = max } };

        JsonSerializer.SerializeToElement(preset, Legacy).GetProperty("targetRange").GetRawText().Should().Be(expected);
    }

    [Fact]
    public void Without_the_modifier_a_range_is_written_as_an_object()
    {
        var preset = new LoopOverridePreset { TargetRange = new LoopTargetRange { MinValue = 100, MaxValue = 110 } };

        JsonSerializer.SerializeToNode(preset)!["targetRange"]!.ToJsonString().Should().Be("""{"minValue":100,"maxValue":110}""");
    }

    [Fact]
    public void The_workout_range_keeps_the_object_form()
    {
        var settings = new LoopProfileSettings
        {
            PreMealTargetRange = new LoopTargetRange { MinValue = 80, MaxValue = 90 },
            WorkoutTargetRange = new LoopTargetRange { MinValue = 140, MaxValue = 160 },
        };

        var json = JsonSerializer.SerializeToNode(settings, Legacy)!;

        json["preMealTargetRange"]!.ToJsonString().Should().Be("[80,90]");
        json["workoutTargetRange"]!.ToJsonString().Should().Be("""{"minValue":140,"maxValue":160}""");
    }
}
