using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class RuleDataNeedsTests
{
    [Fact]
    public void Empty_ruleset_returns_no_needs()
    {
        var result = RuleDataNeeds.Walk(Array.Empty<AlertRuleSnapshot>());

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [Fact]
    public void Threshold_only_triggers_no_optional_needs()
    {
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [Fact]
    public void Trend_leaf_at_top_level_sets_trend_bucket_need()
    {
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"rising_fast"}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsTrendBucket.Should().BeTrue();
        result.NeedsIob.Should().BeFalse();
    }

    [Fact]
    public void Iob_leaf_inside_composite_sets_iob_need()
    {
        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 2 } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsIob.Should().BeTrue();
    }

    [Fact]
    public void Nested_composite_with_multiple_kinds_sets_all_relevant_flags()
    {
        var json = """
        {
          "operator": "and",
          "conditions": [
            { "type": "iob", "iob": { "operator": ">", "value": 2 } },
            { "type": "cob", "cob": { "operator": ">", "value": 30 } },
            { "type": "predicted", "predicted": { "operator": "<", "value": 70, "within_minutes": 30 } },
            { "type": "trend", "trend": { "bucket": "falling_fast" } },
            { "type": "alert_state", "alert_state": { "alert_id": "00000000-0000-0000-0000-000000000001", "state": "firing" } }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsIob.Should().BeTrue();
        result.NeedsCob.Should().BeTrue();
        result.NeedsPredicted.Should().BeTrue();
        result.NeedsTrendBucket.Should().BeTrue();
        result.NeedsActiveAlerts.Should().BeTrue();
        result.NeedsReservoir.Should().BeFalse();
        result.NeedsSiteAge.Should().BeFalse();
        result.NeedsSensorAge.Should().BeFalse();
    }

    [Fact]
    public void Sustained_wrapper_recurses_into_child()
    {
        var json = """
        {
          "minutes": 15,
          "child": { "type": "reservoir", "reservoir": { "operator": "<", "value": 20 } }
        }
        """;
        var rule = MakeRule(AlertConditionType.Sustained, json);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsReservoir.Should().BeTrue();
    }

    [Fact]
    public void Not_wrapper_recurses_into_child()
    {
        var json = """
        {
          "child": { "type": "site_age", "site_age": { "operator": ">", "value": 72 } }
        }
        """;
        var rule = MakeRule(AlertConditionType.Not, json);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsSiteAge.Should().BeTrue();
    }

    [Fact]
    public void Malformed_json_treated_as_no_needs()
    {
        var rule = MakeRule(AlertConditionType.Composite, "{ not valid json");

        var act = () => RuleDataNeeds.Walk(new[] { rule });

        act.Should().NotThrow();
        act().Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [Fact]
    public void Multiple_rules_are_unioned()
    {
        var trendRule = MakeRule(AlertConditionType.Trend, """{"bucket":"flat"}""");
        var iobRule = MakeRule(AlertConditionType.Iob, """{"operator":">","value":1}""");

        var result = RuleDataNeeds.Walk(new[] { trendRule, iobRule });

        result.NeedsTrendBucket.Should().BeTrue();
        result.NeedsIob.Should().BeTrue();
    }

    // ----- Looping kinds -----

    [Fact]
    public void LoopStale_rule_sets_LastApsCycle_only()
    {
        var rule = MakeRule(AlertConditionType.LoopStale, """{"operator":">","minutes":15}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsLastApsCycle.Should().BeTrue();
        result.NeedsLastApsEnacted.Should().BeFalse();
        result.NeedsPumpStatus.Should().BeFalse();
        result.NeedsTempBasal.Should().BeFalse();
        result.NeedsUploaderStatus.Should().BeFalse();
        result.NeedsOverride.Should().BeFalse();
        result.NeedsSensitivityRatio.Should().BeFalse();
    }

    [Fact]
    public void LoopEnactionStale_rule_sets_LastApsEnacted_and_LastApsCycle()
    {
        // Co-fetch invariant: LoopEnactionStale's evaluator reads HasEverApsCycled (the
        // shared cold-start guard, since there is no separate HasEverApsEnacted flag), so
        // RuleDataNeeds must request both timestamps. Without LastApsCycle, a tenant whose
        // only enabled looping rule is LoopEnactionStale would never fire on a healthy loop.
        var rule = MakeRule(AlertConditionType.LoopEnactionStale, """{"operator":">","minutes":15}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsLastApsEnacted.Should().BeTrue();
        result.NeedsLastApsCycle.Should().BeTrue();
    }

    [Fact]
    public void PumpSuspended_rule_sets_PumpStatus_only()
    {
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsPumpStatus.Should().BeTrue();
        result.NeedsLastApsCycle.Should().BeFalse();
    }

    [Fact]
    public void PumpBattery_rule_sets_PumpStatus_only()
    {
        var rule = MakeRule(AlertConditionType.PumpBattery, """{"operator":"<","value":20}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsPumpStatus.Should().BeTrue();
    }

    [Fact]
    public void TempBasal_rule_sets_TempBasal_only()
    {
        var rule = MakeRule(AlertConditionType.TempBasal,
            """{"metric":"rate","operator":">","value":1.5}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsTempBasal.Should().BeTrue();
        result.NeedsPumpStatus.Should().BeFalse();
    }

    [Fact]
    public void UploaderBattery_rule_sets_UploaderStatus_only()
    {
        var rule = MakeRule(AlertConditionType.UploaderBattery, """{"operator":"<","value":20}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsUploaderStatus.Should().BeTrue();
    }

    [Fact]
    public void OverrideActive_rule_sets_Override_only()
    {
        var rule = MakeRule(AlertConditionType.OverrideActive, """{"is_active":true}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsOverride.Should().BeTrue();
    }

    [Fact]
    public void SensitivityRatio_rule_sets_SensitivityRatio_only()
    {
        var rule = MakeRule(AlertConditionType.SensitivityRatio, """{"operator":"<","value":0.8}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsSensitivityRatio.Should().BeTrue();
    }

    [Fact]
    public void Composite_with_loop_stale_inside_not_sustained_sets_LastApsCycle()
    {
        var json = """
        {
          "operator": "and",
          "conditions": [
            {
              "type": "not",
              "not": {
                "child": {
                  "type": "sustained",
                  "sustained": {
                    "minutes": 5,
                    "child": { "type": "loop_stale", "loop_stale": { "operator": ">", "minutes": 15 } }
                  }
                }
              }
            }
          ]
        }
        """;
        var rule = MakeRule(AlertConditionType.Composite, json);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsLastApsCycle.Should().BeTrue();
    }

    private static AlertRuleSnapshot MakeRule(AlertConditionType type, string json) =>
        new(Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Name: "test-rule",
            ConditionType: type,
            ConditionParams: json,
            Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: false,
            AutoResolveParams: null);
}
