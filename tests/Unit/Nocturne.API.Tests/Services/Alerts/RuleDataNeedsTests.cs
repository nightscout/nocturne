using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class RuleDataNeedsTests
{
    [NativeFact]
    public void Empty_ruleset_returns_no_needs()
    {
        var result = RuleDataNeeds.Walk(Array.Empty<AlertRuleSnapshot>());

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [NativeFact]
    public void Threshold_only_triggers_no_optional_needs()
    {
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [NativeFact]
    public void Trend_leaf_at_top_level_sets_trend_bucket_need()
    {
        var rule = MakeRule(AlertConditionType.Trend, """{"bucket":"rising_fast"}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsTrendBucket.Should().BeTrue();
        result.NeedsIob.Should().BeFalse();
    }

    [NativeFact]
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

    [NativeFact]
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

    [NativeFact]
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

    [NativeFact]
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

    [NativeFact]
    public void Malformed_json_treated_as_no_needs()
    {
        var rule = MakeRule(AlertConditionType.Composite, "{ not valid json");

        var act = () => RuleDataNeeds.Walk(new[] { rule });

        act.Should().NotThrow();
        act().Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [NativeFact]
    public void Multiple_rules_are_unioned()
    {
        var trendRule = MakeRule(AlertConditionType.Trend, """{"bucket":"flat"}""");
        var iobRule = MakeRule(AlertConditionType.Iob, """{"operator":">","value":1}""");

        var result = RuleDataNeeds.Walk(new[] { trendRule, iobRule });

        result.NeedsTrendBucket.Should().BeTrue();
        result.NeedsIob.Should().BeTrue();
    }

    // ----- Looping kinds -----

    [NativeFact]
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

    [NativeFact]
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

    [NativeFact]
    public void PumpSuspended_rule_sets_PumpStatus_only()
    {
        var rule = MakeRule(AlertConditionType.PumpSuspended, """{"is_active":true}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsPumpStatus.Should().BeTrue();
        result.NeedsLastApsCycle.Should().BeFalse();
    }

    [NativeFact]
    public void PumpBattery_rule_sets_PumpStatus_only()
    {
        var rule = MakeRule(AlertConditionType.PumpBattery, """{"operator":"<","value":20}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsPumpStatus.Should().BeTrue();
    }

    [NativeFact]
    public void TempBasal_rule_sets_TempBasal_only()
    {
        var rule = MakeRule(AlertConditionType.TempBasal,
            """{"metric":"rate","operator":">","value":1.5}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsTempBasal.Should().BeTrue();
        result.NeedsPumpStatus.Should().BeFalse();
    }

    [NativeFact]
    public void UploaderBattery_rule_sets_UploaderStatus_only()
    {
        var rule = MakeRule(AlertConditionType.UploaderBattery, """{"operator":"<","value":20}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsUploaderStatus.Should().BeTrue();
    }

    [NativeFact]
    public void OverrideActive_rule_sets_Override_only()
    {
        var rule = MakeRule(AlertConditionType.OverrideActive, """{"is_active":true}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsOverride.Should().BeTrue();
    }

    [NativeFact]
    public void SensitivityRatio_rule_sets_SensitivityRatio_only()
    {
        var rule = MakeRule(AlertConditionType.SensitivityRatio, """{"operator":"<","value":0.8}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsSensitivityRatio.Should().BeTrue();
    }

    [NativeFact]
    public void TimeOfDay_rule_sets_TenantTimeZone_so_evaluator_can_fall_back_to_tenant_tz()
    {
        // TimeOfDayEvaluator falls back to SensorContext.TenantTimeZoneId when the rule's
        // own Timezone is null (which is the default the UI saves). Without requesting the
        // tenant timezone here, the enricher would skip the TherapySettings fetch and the
        // evaluator would silently fall back to UTC — firing at the wrong wall-clock hour
        // for non-UTC tenants.
        var rule = MakeRule(AlertConditionType.TimeOfDay,
            """{"from":"10:00","to":"14:00","timezone":null}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsTenantTimeZone.Should().BeTrue();
    }

    [NativeFact]
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

    [NativeTheory]
    [InlineData("""{"operator":"and"}""")]
    [InlineData("""{"operator":"and","conditions":[null]}""")]
    [InlineData("""{"operator":"and","conditions":[{"iob":{"operator":">","value":2}}]}""")]
    [InlineData("""{"operator":"and","conditions":[{"type":"not","not":{}}]}""")]
    [InlineData("""{"operator":"and","conditions":[{"type":"composite","composite":{"operator":"or"}}]}""")]
    public void Malformed_stored_composite_does_not_abort_the_batch(string json)
    {
        var bad = MakeRule(AlertConditionType.Composite, json);
        var iob = MakeRule(AlertConditionType.Iob, """{"operator":">","value":2}""");

        var result = RuleDataNeeds.Walk(new[] { bad, iob });

        result.NeedsIob.Should().BeTrue();
    }

    [NativeFact]
    public void Not_and_sustained_with_null_child_contribute_no_needs()
    {
        var not = MakeRule(AlertConditionType.Not, """{}""");
        var sustained = MakeRule(AlertConditionType.Sustained, """{"minutes":5}""");

        var result = RuleDataNeeds.Walk(new[] { not, sustained });

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    private const string CobZero = """{"type":"cob","cob":{"operator":"==","value":0}}""";

    [NativeFact]
    public void Enabled_auto_resolve_condition_sets_a_need_the_body_does_not()
    {
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""", true, CobZero);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsCob.Should().BeTrue();
    }

    [NativeFact]
    public void Disabled_auto_resolve_condition_contributes_no_needs()
    {
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""", false, CobZero);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.Should().BeEquivalentTo(DataNeedsSet.None);
    }

    [NativeFact]
    public void Nested_auto_resolve_tree_is_walked_with_the_per_node_selections()
    {
        var trackerId = Guid.NewGuid();
        var autoResolve = $$"""
        {
          "type": "composite",
          "composite": {
            "operator": "and",
            "conditions": [
              {
                "type": "not",
                "not": { "child": { "type": "iob", "iob": { "operator": ">", "value": 2 } } }
              },
              { "type": "tracker_age", "tracker_age": { "tracker_definition_id": "{{trackerId}}", "operator": ">", "minutes": 60 } }
            ]
          }
        }
        """;
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""", true, autoResolve);

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.NeedsIob.Should().BeTrue();
        result.ReferencedTrackerDefinitions.Should().BeEquivalentTo(new[] { trackerId });
    }

    [NativeTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    [InlineData("""{"type":"composite","composite":{"operator":"and"}}""")]
    public void Malformed_auto_resolve_does_not_abort_the_batch(string? autoResolve)
    {
        var bad = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""", true, autoResolve);
        var iob = MakeRule(AlertConditionType.Iob, """{"operator":">","value":2}""");

        var result = RuleDataNeeds.Walk(new[] { bad, iob });

        result.NeedsIob.Should().BeTrue();
        result.NeedsCob.Should().BeFalse();
    }

    [NativeTheory]
    [InlineData("""{"mode":8,"is_active":true}""")]
    [InlineData("""{"mode":"suspended","is_active":true}""")]
    public void A_pump_mode_that_is_not_a_member_name_selects_no_mode(string conditionParams)
    {
        var rule = MakeRule(AlertConditionType.PumpState, conditionParams);

        RuleDataNeeds.Walk(new[] { rule }).ReferencedPumpStates.Should().BeEmpty();
    }

    private static AlertRuleSnapshot MakeRule(
        AlertConditionType type, string json, bool autoResolveEnabled = false, string? autoResolveParams = null) =>
        new(Id: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Name: "test-rule",
            ConditionType: type,
            ConditionParams: json,
            Severity: AlertRuleSeverity.Warning,
            ClientConfiguration: "{}",
            SortOrder: 0,
            AutoResolveEnabled: autoResolveEnabled,
            AutoResolveParams: autoResolveParams);
}
