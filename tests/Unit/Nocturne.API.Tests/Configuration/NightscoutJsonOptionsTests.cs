using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Configuration;

/// <summary><see cref="NightscoutJsonOptions.OmitIndefiniteDuration"/>.</summary>
public class NightscoutJsonOptionsTests
{
    private static JsonObject Serialize(Treatment treatment) =>
        JsonNode.Parse(JsonSerializer.Serialize(treatment, NightscoutJsonOptions.Create()))!.AsObject();

    [Fact]
    public void An_indefinite_override_without_a_duration_is_served_without_one()
    {
        var served = Serialize(new Treatment { EventType = "Temporary Override", DurationType = "indefinite" });

        served.ContainsKey("duration").Should().BeFalse();
        served["durationType"]!.GetValue<string>().Should().Be("indefinite");
    }

    /// <summary>AAPS reads a zero-duration Temporary Target as a cancel and a zero-duration Profile Switch as permanent.</summary>
    [Theory]
    [InlineData("Temporary Target")]
    [InlineData("Profile Switch")]
    [InlineData("Note")]
    public void A_zero_duration_on_a_treatment_that_is_not_indefinite_is_served(string eventType)
    {
        var served = Serialize(new Treatment { EventType = eventType, Duration = 0 });

        served["duration"]!.GetValue<double>().Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, "false", "false")]
    [InlineData(true, true, "true", "true")]
    [InlineData(false, true, "false", "true")]
    public void Food_flags_are_served_as_strings_and_always_present(
        bool hidden, bool hideAfterUse, string expectedHidden, string expectedHideAfterUse)
    {
        var served = JsonNode.Parse(
            JsonSerializer.Serialize(
                new Food { Name = "Snack", Hidden = hidden, HideAfterUse = hideAfterUse },
                NightscoutJsonOptions.Create()))!.AsObject();

        served["hidden"]!.GetValue<string>().Should().Be(expectedHidden);
        served["hideafteruse"]!.GetValue<string>().Should().Be(expectedHideAfterUse);
    }

    [Fact]
    public void Food_flags_read_back_from_the_strings_they_are_served_as()
    {
        var food = JsonSerializer.Deserialize<Food>("""{"hidden":"true","hideafteruse":"false"}""", NightscoutJsonOptions.Create())!;

        food.Hidden.Should().BeTrue();
        food.HideAfterUse.Should().BeFalse();
    }

    [Fact]
    public void A_finite_duration_on_an_indefinite_override_is_served()
    {
        var served = Serialize(new Treatment
        {
            EventType = "Temporary Override", DurationType = "indefinite", Duration = 25,
        });

        served["duration"]!.GetValue<double>().Should().Be(25);
    }

    private static JsonNode ServedProfile(string loopSettings) =>
        JsonNode.Parse(
            JsonSerializer.Serialize(
                JsonSerializer.Deserialize<Profile>($$"""{"loopSettings":{{loopSettings}}}""")!,
                NightscoutJsonOptions.Create()))!["loopSettings"]!;

    [Theory]
    [InlineData("[100,110]")]
    [InlineData("""{"minValue":100,"maxValue":110}""")]
    public void A_loop_target_range_is_served_as_the_array_Loop_uploads_whichever_form_was_stored(string stored)
    {
        var served = ServedProfile($$"""{"overridePresets":[{"name":"Run","targetRange":{{stored}}}]}""");

        served["overridePresets"]![0]!["targetRange"]!.ToJsonString().Should().Be("[100,110]");
    }

    [Fact]
    public void The_ranges_Loop_uploads_as_arrays_are_served_as_arrays_and_the_rest_keep_the_object_form()
    {
        var served = ServedProfile(
            """
            {"preMealTargetRange":[80,90],"workoutTargetRange":{"minValue":140,"maxValue":160},
             "scheduleOverride":{"name":"Now","targetRange":[120,130]}}
            """);

        served["preMealTargetRange"]!.ToJsonString().Should().Be("[80,90]");
        served["scheduleOverride"]!["targetRange"]!.ToJsonString().Should().Be("[120,130]");
        served["workoutTargetRange"]!.ToJsonString().Should().Be("""{"minValue":140,"maxValue":160}""");
    }

    [Fact]
    public void A_preset_without_a_target_range_is_served_without_one()
    {
        var served = ServedProfile("""{"overridePresets":[{"name":"Plain"}]}""");

        served["overridePresets"]![0]!.AsObject().ContainsKey("targetRange").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"minValue":100}""")]
    [InlineData("""{"maxValue":110}""")]
    [InlineData("{}")]
    public void A_target_range_without_both_bounds_is_served_as_null(string stored)
    {
        var served = ServedProfile($$"""{"overridePresets":[{"targetRange":{{stored}}}]}""");

        served["overridePresets"]![0]!.AsObject().TryGetPropertyValue("targetRange", out var range).Should().BeTrue();
        range.Should().BeNull();
    }

    [Theory]
    [InlineData("[100]")]
    [InlineData("[]")]
    [InlineData("[100,110,120]")]
    public void An_array_that_is_not_two_numbers_is_served_without_a_target_range(string stored)
    {
        var served = ServedProfile($$"""{"overridePresets":[{"targetRange":{{stored}}}]}""");

        served["overridePresets"]![0]!.AsObject().ContainsKey("targetRange").Should().BeFalse();
    }
}
