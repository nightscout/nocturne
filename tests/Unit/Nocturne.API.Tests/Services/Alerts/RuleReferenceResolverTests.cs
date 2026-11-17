using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class RuleReferenceResolverTests
{
    [NativeFact]
    public void Empty_input_returns_empty()
    {
        RuleReferenceResolver.FilterEvaluable(Array.Empty<AlertRuleSnapshot>())
            .Should().BeEmpty();
    }

    [NativeFact]
    public void Rule_with_no_alert_state_references_kept()
    {
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [NativeFact]
    public void Top_level_alert_state_referencing_enabled_rule_kept()
    {
        var parentId = Guid.NewGuid();
        var parent = MakeRule(parentId, AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{parentId}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { parent, child });

        result.Should().HaveCount(2);
    }

    [NativeFact]
    public void Top_level_alert_state_referencing_unknown_rule_dropped()
    {
        var parent = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{Guid.NewGuid()}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { parent, child });

        result.Should().ContainSingle().Which.Should().Be(parent);
    }

    [NativeFact]
    public void Alert_state_referencing_an_enabled_rule_outside_the_batch_kept()
    {
        var parentId = Guid.NewGuid();
        var child = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{parentId}}","state":"firing"}""");

        RuleReferenceResolver.FilterEvaluable(new[] { child }, enabledIds: new HashSet<Guid> { parentId, child.Id })
            .Should().ContainSingle().Which.Should().Be(child);
    }

    [NativeFact]
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

    [NativeFact]
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

    [NativeFact]
    public void Self_reference_treated_as_evaluable()
    {
        var id = Guid.NewGuid();
        var rule = MakeRule(id, AlertConditionType.AlertState,
            $$"""{"alert_id":"{{id}}","state":"firing"}""");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [NativeFact]
    public void Malformed_json_does_not_drop_rule()
    {
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Composite, "{ broken json");

        RuleReferenceResolver.FilterEvaluable(new[] { rule })
            .Should().ContainSingle().Which.Should().Be(rule);
    }

    [NativeFact]
    public void Order_preserved()
    {
        var a = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");
        var b = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"below","value":70}""");
        var c = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState,
            $$"""{"alert_id":"{{a.Id}}","state":"firing"}""");

        var result = RuleReferenceResolver.FilterEvaluable(new[] { a, b, c });

        result.Select(r => r.Id).Should().Equal(a.Id, b.Id, c.Id);
    }

    [NativeTheory]
    [InlineData("""{"operator":"and"}""")]
    [InlineData("""{"operator":"and","conditions":[null]}""")]
    [InlineData("""{"operator":"and","conditions":[{"alert_state":{"state":"firing"}}]}""")]
    [InlineData("""{"operator":"and","conditions":[{"type":"sustained","sustained":{"minutes":5}}]}""")]
    public void Malformed_stored_composite_is_left_for_the_engine_to_skip(string json)
    {
        var bad = MakeRule(Guid.NewGuid(), AlertConditionType.Composite, json);
        var ok = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        RuleReferenceResolver.FilterEvaluable(new[] { bad, ok })
            .Should().Equal(bad, ok);
    }

    [NativeFact]
    public void An_alert_state_in_the_auto_resolve_tree_does_not_drop_the_rule()
    {
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.Threshold, """{"direction":"above","value":180}""") with
        {
            AutoResolveEnabled = true,
            AutoResolveParams = $$$"""{"type":"alert_state","alert_state":{"alert_id":"{{{Guid.NewGuid()}}}","state":"firing"}}""",
        };

        RuleReferenceResolver.FilterEvaluable(new[] { rule }).Should().Equal(rule);
    }

    [NativeFact]
    public void A_body_that_does_not_read_names_no_reference()
    {
        // Evaluation rejects the rule; this filter only drops references that do not resolve.
        var rule = MakeRule(Guid.NewGuid(), AlertConditionType.AlertState, """{"alert_id":"garbage","state":"active"}""");

        RuleReferenceResolver.FilterEvaluable(new[] { rule }).Should().ContainSingle();
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
