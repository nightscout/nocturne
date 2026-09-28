using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class SmartSnoozeConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"audio":{"enabled":true}}""")]
    public void MissingSection_UsesDefaults(string? clientConfiguration)
    {
        var config = SmartSnoozeConfig.Parse(clientConfiguration);

        config.Should().Be(SmartSnoozeConfig.Default);
        config.SmartSnooze.Should().BeFalse();
        config.MaxCount.Should().Be(3);
        config.ExtendMinutes.Should().Be(10);
        config.Conditions.Should().BeNull();
        config.Malformed.Should().BeFalse();
    }

    [Fact]
    public void FullSection_IsRead()
    {
        var config = SmartSnoozeConfig.Parse("""
            {"snooze":{"smartSnooze":true,"smartSnoozeExtendMinutes":20,"maxCount":4,
              "conditions":[{"type":"threshold","threshold":{"direction":"above","value":70}}]}}
            """);

        config.SmartSnooze.Should().BeTrue();
        config.ExtendMinutes.Should().Be(20);
        config.MaxCount.Should().Be(4);
        config.Conditions.Should().ContainSingle()
            .Which.Threshold!.Direction.Should().Be("above");
        config.Malformed.Should().BeFalse();
    }

    [Fact]
    public void FieldOfTheWrongKind_FallsBackAloneAndFlagsMalformed()
    {
        // The previous parser threw InvalidOperationException here, outside its JsonException
        // catch, which aborted the whole sweep for every tenant.
        var config = SmartSnoozeConfig.Parse("""
            {"snooze":{"smartSnooze":true,"smartSnoozeExtendMinutes":"20","maxCount":"4"}}
            """);

        config.SmartSnooze.Should().BeTrue();
        config.ExtendMinutes.Should().Be(SmartSnoozeConfig.DefaultExtendMinutes);
        config.MaxCount.Should().Be(SmartSnoozeConfig.DefaultMaxCount);
        config.Malformed.Should().BeTrue();
    }

    [Fact]
    public void NonBooleanSmartSnooze_IsOff()
    {
        var config = SmartSnoozeConfig.Parse("""{"snooze":{"smartSnooze":"yes"}}""");

        config.SmartSnooze.Should().BeFalse();
        config.Malformed.Should().BeTrue();
    }

    [Fact]
    public void ZeroMaxCount_IsHonoured_NegativeIsNot()
    {
        SmartSnoozeConfig.Parse("""{"snooze":{"maxCount":0}}""").MaxCount.Should().Be(0);
        SmartSnoozeConfig.Parse("""{"snooze":{"maxCount":-1}}""").MaxCount
            .Should().Be(SmartSnoozeConfig.DefaultMaxCount);
    }

    [Fact]
    public void NonPositiveExtendMinutes_UsesDefault()
    {
        SmartSnoozeConfig.Parse("""{"snooze":{"smartSnoozeExtendMinutes":0}}""").ExtendMinutes
            .Should().Be(SmartSnoozeConfig.DefaultExtendMinutes);
    }

    [Fact]
    public void InvalidJson_UsesDefaultsAndFlagsMalformed()
    {
        var config = SmartSnoozeConfig.Parse("{not json");

        config.SmartSnooze.Should().BeFalse();
        config.Malformed.Should().BeTrue();
    }

    [Fact]
    public void UnreadableConditions_NeverFallThroughToTheTrendFallback()
    {
        var config = SmartSnoozeConfig.Parse("""
            {"snooze":{"smartSnooze":true,"conditions":[{"type":"threshold","threshold":"oops"}]}}
            """);

        config.Conditions.Should().NotBeNullOrEmpty(
            "an empty list would route the instance to the trend fallback the user did not configure");
        config.Malformed.Should().BeTrue();
    }
}
