using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class RuleReferenceResolverTests
{
    [Fact]
    public void Empty_input_returns_empty()
    {
        RuleReferenceResolver.FilterEvaluable(Array.Empty<AlertRuleSnapshot>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Rule_with_no_alert_state_references_kept()
    {
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [Fact]
    public void Top_level_alert_state_referencing_enabled_rule_kept()
    {
        var parentId = Guid.NewGuid();
        var parent = MakeRule(parentId, AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{parentId}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { parent, child });

        result.Should().HaveCount(2);
    }

    [Fact]
    public void Top_level_alert_state_referencing_unknown_rule_dropped()
    {
        var parent = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{Guid.NewGuid()}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { parent, child });

        result.Should().ContainSingle().Which.Should().Be(parent);
    }

    [Fact]
    public void Nested_alert_state_inside_composite_excludes_chain_when_unresolved()
    {
        var orphan = Guid.NewGuid();
        var json = $$"""
        {
          "operator": "and",
          "conditions": [
            { "type": "threshold", "threshold": { "direction": "above", "value": 180 } },
            { "type": "alert_state", "alert_state": { "alert_id": "{{orphan}}", "state": "firing" } }
          ]
        }
        """;
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Composite, json);

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().BeEmpty();
    }

    [Fact]
    public void Nested_alert_state_inside_sustained_kept_when_resolved()
    {
        var parentId = Guid.NewGuid();
        var parent = MakeRule(parentId, AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var json = $$"""
        {
          "minutes": 15,
          "child": { "type": "alert_state", "alert_state": { "alert_id": "{{parentId}}", "state": "firing" } }
        }
        """;
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.Sustained, json);

        RuleReferenceResolver.FilterEvaluable(new[] { parent, child })
            .Should().HaveCount(2);
    }

    [Fact]
    public void Self_reference_treated_as_evaluable()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id, AlertConditionType.AlertState,
            $$"""{"alert_id":"{{id}}","state":"firing"}""");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [Fact]
    public void Malformed_json_does_not_drop_rule()
    {
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Composite, "{ broken json");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [Fact]
    public void Order_preserved()
    {
        var a = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var b = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"below","value":70}""");
        var c = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{a.Id}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { a, b, c });

        result.Select(r => r.Id).Should().Equal(a.Id, b.Id, c.Id);
    }

    private static AlertRuleSnapshot MakeRule(Guid id, AlertConditionType type, string json) =>
        new(Id: id,
            TenantId: Guid.NewGuid(),
            Name: "test",
            ConditionType: type,
            ConditionParams: json,
            Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: false,
            AutoResolveParams: null);
}
