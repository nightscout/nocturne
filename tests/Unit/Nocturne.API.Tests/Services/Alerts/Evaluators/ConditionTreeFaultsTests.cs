using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class ConditionTreeFaultsTests
{
    private const string Low = """{"type": "threshold", "threshold": {"direction": "below", "value": 70}}""";

    [Theory]
    [InlineData("""{"operator": "and"}""", "composite", "conditions_missing")]
    [InlineData("""{"conditions": [LOW]}""", "composite", "operator_missing")]
    [InlineData("""{"operator": "or", "conditions": [LOW, null]}""", "composite[1].", "condition_missing")]
    [InlineData("""{"operator": "and", "conditions": [{"threshold": {"direction": "below"}}]}""", "composite[0].", "type_missing")]
    [InlineData("""{"operator": "and", "conditions": [{"type": "sustained"}, {"type": "composite"}]}""", "composite[1].composite", "conditions_missing")]
    [InlineData("""{"operator": "and", "conditions": [{"type": "RateOfChange", "rate_of_change": {"direction": "falling", "rate": 2}}]}""", "composite[0].RateOfChange", "direction_missing")]
    [InlineData("""{"operator": "and", "conditions": [{"type": "sustained", "sustained": {"minutes": 0, "child": {"type": "alert_state"}}}]}""", "composite[0].sustained[0].alert_state", "state_missing")]
    public void Composite_rule_faults_are_found_wherever_they_sit(string json, string path, string reason)
    {
        var fault = ConditionTreeFaults.InRule(AlertConditionType.Composite, json.Replace("LOW", Low));

        fault.Should().Be(new ConditionTreeFault(path, reason));
    }

    [Theory]
    [InlineData(AlertConditionType.Threshold, """{"value": 70}""", "direction_missing")]
    [InlineData(AlertConditionType.RateOfChange, """{}""", "direction_missing")]
    [InlineData(AlertConditionType.AlertState, """{"alert_id": "00000000-0000-0000-0000-0000000000aa"}""", "state_missing")]
    [InlineData(AlertConditionType.Not, """{"child": {"type": null}}""", "type_missing")]
    public void Leaf_and_wrapper_rule_faults_are_found(AlertConditionType type, string json, string reason)
    {
        ConditionTreeFaults.InRule(type, json)!.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData(AlertConditionType.Composite, """{"operator": "xor", "conditions": [{"type": "warp_drive"}]}""")]
    [InlineData(AlertConditionType.Composite, """{"conditions": []}""")]
    [InlineData(AlertConditionType.Not, """{}""")]
    [InlineData(AlertConditionType.Threshold, """{"direction": "sideways", "value": 70}""")]
    [InlineData(AlertConditionType.Threshold, "null")]
    [InlineData(AlertConditionType.Threshold, "not json")]
    [InlineData(AlertConditionType.Iob, """{}""")]
    public void Trees_that_evaluate_without_throwing_have_no_fault(AlertConditionType type, string json)
    {
        ConditionTreeFaults.InRule(type, json).Should().BeNull();
    }

    [Fact]
    public void Node_faults_are_rooted_at_the_scope()
    {
        var node = JsonSerializer.Deserialize<ConditionNode>(
            """{"type": "composite", "composite": {"operator": "and", "conditions": [{"type": "threshold"}]}}""",
            EvaluatorJson.Options)!;

        ConditionTreeFaults.InNode(node, "auto_resolve")
            .Should().Be(new ConditionTreeFault("auto_resolve[0].threshold", "direction_missing"));
        ConditionTreeFaults.InNode(new ConditionNode(null!), "snooze")
            .Should().Be(new ConditionTreeFault("snooze", "type_missing"));
    }
}
