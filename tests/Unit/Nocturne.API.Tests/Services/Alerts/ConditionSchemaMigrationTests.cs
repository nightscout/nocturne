using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;
using Nocturne.Tests.Shared.Logging;
using Xunit;
using StoredRule = Nocturne.API.Services.Alerts.ConditionSchemaMigration.StoredRule;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// One rule's stored trees through <see cref="ConditionSchemaMigration.Migrate"/> and the engine's own
/// migration (docs/alerts/engine-semantics.md §1.5).
/// </summary>
[Trait("Category", "Unit")]
public class ConditionSchemaMigrationTests
{
    private readonly ListLogger _logger = new();

    private ConditionSchemaMigration.MigratedRule Migrate(StoredRule rule) =>
        ConditionSchemaMigration.Migrate(rule, RustAlertEngine.Migrate, _logger);

    private static StoredRule Rule(
        string conditionType = "threshold",
        string conditionParams = """{"direction":"below","value":70}""",
        string? autoResolve = null,
        string clientConfiguration = "{}",
        string? preRepair = null,
        string? reviewReason = null) => new()
        {
            Id = Guid.NewGuid(),
            ConditionType = conditionType,
            ConditionParams = conditionParams,
            AutoResolveParams = autoResolve,
            ClientConfiguration = clientConfiguration,
            PreRepair = preRepair,
            ReviewReason = reviewReason,
        };

    private static JsonNode Json(string json) => JsonNode.Parse(json)!;

    [NativeFact]
    public void Every_tree_of_a_rule_moves_to_schema_2()
    {
        var migrated = Migrate(Rule(
            autoResolve: """{"type":"threshold","threshold":{"direction":"above","value":80}}""",
            clientConfiguration: """
                {"snooze":{"smartSnooze":true,"maxCount":2,"conditions":[{"type":"rate_of_change","rate_of_change":{"direction":"rising","rate":1}}]},"ui":{"colour":"red"}}
                """,
            preRepair: """
                {"condition_type":"staleness","condition_params":{"operator":">","value":20},"auto_resolve_params":null,"severity":"warning","is_enabled":true}
                """));

        migrated.Changed.Should().BeTrue();
        migrated.Rounded.Should().BeFalse();
        migrated.Failed.Should().Be(0);
        migrated.ReviewReason.Should().BeNull();
        JsonNode.DeepEquals(Json(migrated.ConditionParams), Json("""{"schema_version":2,"operator":"<","value":70}""")).Should().BeTrue();
        JsonNode.DeepEquals(
            Json(migrated.AutoResolveParams!),
            Json("""{"schema_version":2,"type":"threshold","threshold":{"operator":">","value":80}}""")).Should().BeTrue();
        JsonNode.DeepEquals(Json(migrated.ClientConfiguration), Json("""
            {"snooze":{"smartSnooze":true,"maxCount":2,"conditions":[{"schema_version":2,"type":"rate_of_change","rate_of_change":{"operator":">=","value":1}}]},"ui":{"colour":"red"}}
            """)).Should().BeTrue();
        JsonNode.DeepEquals(Json(migrated.PreRepair!), Json("""
            {"condition_type":"staleness","condition_params":{"schema_version":2,"operator":">","minutes":20},"auto_resolve_params":null,"severity":"warning","is_enabled":true}
            """)).Should().BeTrue();
    }

    [NativeFact]
    public void A_second_run_changes_nothing()
    {
        var first = Migrate(Rule(
            autoResolve: """{"type":"not","not":{"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}}""",
            clientConfiguration: """{"snooze":{"conditions":[{"type":"trend","trend":{"bucket":"rising"}}]}}"""));

        var second = Migrate(Rule(
            conditionParams: first.ConditionParams,
            autoResolve: first.AutoResolveParams,
            clientConfiguration: first.ClientConfiguration));

        second.Changed.Should().BeFalse();
        second.Failed.Should().Be(0);
        second.ConditionParams.Should().Be(first.ConditionParams);
        second.AutoResolveParams.Should().Be(first.AutoResolveParams);
        second.ClientConfiguration.Should().Be(first.ClientConfiguration);
    }

    [NativeFact]
    public void A_site_age_that_is_not_a_whole_minute_asks_for_review()
    {
        var migrated = Migrate(Rule("site_age", """{"operator":">=","value":72.5083}"""));

        migrated.Rounded.Should().BeTrue();
        migrated.ReviewReason.Should().Be(AlertRuleReviewReasons.ConditionDurationRounded);
        JsonNode.DeepEquals(Json(migrated.ConditionParams), Json("""{"schema_version":2,"operator":">=","minutes":4350}"""))
            .Should().BeTrue("72.5083 hours is 4350.498 minutes, and >= fires no later at 4350");
    }

    [NativeFact]
    public void A_rounded_rule_keeps_the_review_reason_it_already_has()
    {
        var migrated = Migrate(Rule(
            "sensor_age", """{"operator":">","value":9.9999}""",
            reviewReason: AlertRuleReviewReasons.LegacyConditionConverted));

        migrated.Rounded.Should().BeTrue();
        migrated.ReviewReason.Should().Be(AlertRuleReviewReasons.LegacyConditionConverted);
    }

    [NativeFact]
    public void A_rounded_tree_in_pre_repair_asks_for_no_review()
    {
        var migrated = Migrate(Rule(
            conditionParams: """{"schema_version":2,"operator":"<","value":70}""",
            preRepair: """{"condition_type":"site_age","condition_params":{"operator":">=","value":0.5083}}"""));

        migrated.Changed.Should().BeTrue();
        migrated.Rounded.Should().BeFalse();
        migrated.ReviewReason.Should().BeNull();
    }

    [NativeFact]
    public void A_body_that_cannot_be_read_is_left_and_named_by_its_codes_only()
    {
        var rule = Rule(
            conditionParams: """{"direction":"sideways","value":70}""",
            autoResolve: """{"type":"threshold","threshold":{"direction":"above","value":80}}""");

        var migrated = Migrate(rule);

        migrated.ConditionParams.Should().Be(rule.ConditionParams);
        migrated.Failed.Should().Be(1);
        migrated.Changed.Should().BeTrue("the auto-resolve tree still moves");
        var (message, properties) = _logger.WarningEvents.Should().ContainSingle().Subject;
        properties["Scope"].Should().Be("condition");
        properties["RuleId"].Should().Be(rule.Id);
        properties["Reason"].Should().Be("unknown_value");
        properties["Path"].Should().Be("threshold");
        properties["Field"].Should().Be("direction");
        message.Should().NotContain("sideways");
    }

    [NativeFact]
    public void Only_the_snooze_conditions_that_can_be_read_move()
    {
        var migrated = Migrate(Rule(
            conditionParams: """{"schema_version":2,"operator":"<","value":70}""",
            clientConfiguration: """
                {"snooze":{"conditions":[{"type":"threshold","threshold":{"direction":"below","value":90}},{"type":"warp_drive","warp_drive":{}}]}}
                """));

        migrated.Failed.Should().Be(1);
        var conditions = Json(migrated.ClientConfiguration)["snooze"]!["conditions"]!.AsArray();
        conditions[0]!["schema_version"]!.GetValue<int>().Should().Be(2);
        JsonNode.DeepEquals(conditions[1], Json("""{"type":"warp_drive","warp_drive":{}}""")).Should().BeTrue();
        _logger.WarningEvents.Should().ContainSingle().Which.Properties["Scope"].Should().Be("snooze[1]");
    }

    [NativeFact]
    public void A_pre_repair_tree_of_a_retired_kind_is_left_without_a_warning()
    {
        var preRepair = """{"condition_type":"legacy_low","condition_params":{"threshold":3.9},"severity":"urgent"}""";

        var migrated = Migrate(Rule(
            conditionParams: """{"schema_version":2,"operator":"<","value":70}""",
            preRepair: preRepair));

        migrated.Changed.Should().BeFalse();
        migrated.PreRepair.Should().Be(preRepair);
        _logger.WarningEvents.Should().BeEmpty();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug);
    }

    [NativeFact]
    public void A_rule_with_no_auto_resolve_and_no_snooze_keeps_them_absent()
    {
        var migrated = Migrate(Rule(autoResolve: "null", clientConfiguration: """{"snooze":{"smartSnooze":false}}"""));

        migrated.AutoResolveParams.Should().Be("null");
        migrated.ClientConfiguration.Should().Be("""{"snooze":{"smartSnooze":false}}""");
        migrated.Failed.Should().Be(0);
    }
}
