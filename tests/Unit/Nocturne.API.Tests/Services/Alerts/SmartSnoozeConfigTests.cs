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
        config.Conditions!.Value.GetRawText().Should().Be(
            """[{"type":"threshold","threshold":{"direction":"above","value":70}}]""");
        config.Malformed.Should().BeFalse();
    }

    [Fact]
    public void Conditions_AreKeptAsWritten_ForTheEngineToRead()
    {
        var config = SmartSnoozeConfig.Parse("""
            {"snooze":{"smartSnooze":true,"conditions":[{"Type":"threshold","threshold":{"Direction":"ABOVE"}}]}}
            """);

        config.Conditions!.Value.GetRawText().Should().Be(
            """[{"Type":"threshold","threshold":{"Direction":"ABOVE"}}]""");
        config.ConditionsUnreadable.Should().BeFalse();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void AnEmptyOrNullConditionList_IsNone(string conditions)
    {
        var config = SmartSnoozeConfig.Parse(
            $$$"""{"snooze":{"smartSnooze":true,"conditions":{{{conditions}}}}}""");

        config.Conditions.Should().BeNull();
        config.ConditionsUnreadable.Should().BeFalse();
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

    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("{}")]
    public void ConditionsThatAreNotAList_NeverFallThroughToTheTrendFallback(string conditions)
    {
        var config = SmartSnoozeConfig.Parse(
            $$$"""{"snooze":{"smartSnooze":true,"conditions":{{{conditions}}}}}""");

        config.ConditionsUnreadable.Should().BeTrue();
        config.Malformed.Should().BeTrue();
    }
}
