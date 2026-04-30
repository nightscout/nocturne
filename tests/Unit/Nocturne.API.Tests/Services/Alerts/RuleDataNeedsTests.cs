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

        result.Should().Be(DataNeedsSet.None);
    }

    [Fact]
    public void Threshold_only_triggers_no_optional_needs()
    {
        var rule = MakeRule(AlertConditionType.Threshold, """{"direction":"above","value":180}""");

        var result = RuleDataNeeds.Walk(new[] { rule });

        result.Should().Be(DataNeedsSet.None);
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
        act().Should().Be(DataNeedsSet.None);
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
