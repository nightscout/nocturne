using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class WallClockConditionsTests
{
    [Fact]
    public void Every_condition_type_is_classified_exactly_once()
    {
        var all = Enum.GetValues<AlertConditionType>();

        WallClockConditions.Kinds.Intersect(WallClockConditions.NotWallClock).Should().BeEmpty();
        WallClockConditions.Kinds.Union(WallClockConditions.NotWallClock)
            .Should().BeEquivalentTo(all, "a new condition kind must be placed on one side");
    }

    [Theory]
    [InlineData(AlertConditionType.SignalLoss, """{"timeout_minutes": 15}""")]
    [InlineData(AlertConditionType.Staleness, """{"operator": ">", "value": 20}""")]
    [InlineData(AlertConditionType.TrackerAge, "not json")]
    public void Wall_clock_root_is_selected_without_parsing(AlertConditionType type, string conditionParams) =>
        WallClockConditions.ReferencesWallClock(Rule(type, conditionParams)).Should().BeTrue();

    [Fact]
    public void Reading_driven_root_is_not_selected() =>
        WallClockConditions.ReferencesWallClock(
                Rule(AlertConditionType.Threshold, """{"direction": "below", "value": 70}"""))
            .Should().BeFalse();

    [Theory]
    [InlineData("""{"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}""")]
    [InlineData("""{"type": "not", "not": {"child": {"type": "staleness", "staleness": {"operator": ">", "value": 5}}}}""")]
    [InlineData("""{"type": "sustained", "sustained": {"minutes": 10, "child": {"type": "loop_stale", "loop_stale": {"operator": ">", "minutes": 30}}}}""")]
    [InlineData("""{"type": "SignalLoss", "signal_loss": {"timeout_minutes": 15}}""")]
    public void Wall_clock_leaf_anywhere_in_the_tree_is_selected(string child)
    {
        var tree = """{"operator": "and", "conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 70}}, """
                   + child + "]}";

        WallClockConditions.ReferencesWallClock(Rule(AlertConditionType.Composite, tree)).Should().BeTrue();
    }

    [Fact]
    public void Tree_of_reading_driven_leaves_and_calendar_gates_is_not_selected()
    {
        const string tree = """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}},
              {"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "iob", "iob": {"operator": ">", "value": 2}}}}
            ]}
            """;

        WallClockConditions.ReferencesWallClock(Rule(AlertConditionType.Composite, tree)).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Unparseable_container_is_not_selected(string conditionParams) =>
        WallClockConditions.ReferencesWallClock(Rule(AlertConditionType.Composite, conditionParams))
            .Should().BeFalse();

    private static AlertRuleSnapshot Rule(AlertConditionType type, string conditionParams) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "rule", type, conditionParams,
            AlertRuleSeverity.Warning, "{}", 0, false, null);
}
