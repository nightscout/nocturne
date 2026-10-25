using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary><see cref="RuleTrees.ReferencesWallClock"/>: which rules the sweep evaluates on the clock.</summary>
[Trait("Category", "Unit")]
public class RuleTreesWallClockTests
{
    [NativeTheory]
    [InlineData(AlertConditionType.SignalLoss, """{"timeout_minutes": 15}""")]
    [InlineData(AlertConditionType.Staleness, """{"operator": ">", "value": 20}""")]
    [InlineData(AlertConditionType.TrackerAge, "not json")]
    public void Wall_clock_root_is_selected_without_parsing(AlertConditionType type, string conditionParams) =>
        RuleTrees.ReferencesWallClock(Rule(type, conditionParams)).Should().BeTrue();

    [NativeFact]
    public void Reading_driven_root_is_not_selected() =>
        RuleTrees.ReferencesWallClock(
                Rule(AlertConditionType.Threshold, """{"direction": "below", "value": 70}"""))
            .Should().BeFalse();

    [NativeTheory]
    [InlineData("""{"type": "signal_loss", "signal_loss": {"timeout_minutes": 15}}""")]
    [InlineData("""{"type": "not", "not": {"child": {"type": "staleness", "staleness": {"operator": ">", "value": 5}}}}""")]
    [InlineData("""{"type": "sustained", "sustained": {"minutes": 10, "child": {"type": "loop_stale", "loop_stale": {"operator": ">", "minutes": 30}}}}""")]
    public void Wall_clock_leaf_anywhere_in_the_tree_is_selected(string child)
    {
        var tree = """{"operator": "and", "conditions": [{"type": "threshold", "threshold": {"direction": "below", "value": 70}}, """
                   + child + "]}";

        RuleTrees.ReferencesWallClock(Rule(AlertConditionType.Composite, tree)).Should().BeTrue();
    }

    [NativeFact]
    public void Tree_of_reading_driven_leaves_and_calendar_gates_is_not_selected()
    {
        const string tree = """
            {"operator": "and", "conditions": [
              {"type": "threshold", "threshold": {"direction": "below", "value": 70}},
              {"type": "time_of_day", "time_of_day": {"from": "22:00", "to": "06:00", "timezone": null}},
              {"type": "sustained", "sustained": {"minutes": 15, "child": {"type": "iob", "iob": {"operator": ">", "value": 2}}}}
            ]}
            """;

        RuleTrees.ReferencesWallClock(Rule(AlertConditionType.Composite, tree)).Should().BeFalse();
    }

    [NativeTheory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Unparseable_container_is_not_selected(string conditionParams) =>
        RuleTrees.ReferencesWallClock(Rule(AlertConditionType.Composite, conditionParams))
            .Should().BeFalse();

    [NativeTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Wall_clock_leaf_in_the_auto_resolve_tree_counts_while_auto_resolve_is_enabled(
        bool autoResolveEnabled, bool expected)
    {
        var rule = Rule(AlertConditionType.Threshold, """{"direction": "below", "value": 70}""") with
        {
            AutoResolveEnabled = autoResolveEnabled,
            AutoResolveParams = """{"type": "staleness", "staleness": {"operator": ">", "value": 20}}""",
        };

        RuleTrees.ReferencesWallClock(rule).Should().Be(expected);
    }

    private static AlertRuleSnapshot Rule(AlertConditionType type, string conditionParams) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "rule", type, conditionParams,
            AlertRuleSeverity.Warning, "{}", 0, false, null);
}
