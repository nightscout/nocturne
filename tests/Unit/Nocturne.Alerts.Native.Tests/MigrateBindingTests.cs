using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// <c>nocturne_alerts_migrate</c> across the binding: one stored tree in, its schema 2 rewrite,
/// "current" or the point it could not be read out (docs/alerts/engine-semantics.md §1.5).
/// </summary>
public class MigrateBindingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [NativeFact]
    public void A_schema_1_body_is_rewritten_and_names_schema_2()
    {
        var response = RustAlertEngine.Migrate(
            RustMigrateRequest.Body("threshold", Json("""{"direction":"below","value":70}""")));

        response.Outcome.Should().Be(RustMigrateOutcome.Migrated);
        response.Rounded.Should().BeFalse();
        var tree = response.Tree!.Value;
        tree.GetProperty("schema_version").GetInt32().Should().Be(2);
        tree.GetProperty("operator").GetString().Should().Be("<");
        tree.GetProperty("value").GetDecimal().Should().Be(70m);
        tree.TryGetProperty("direction", out _).Should().BeFalse();
    }

    [NativeFact]
    public void A_full_node_names_schema_2_at_its_root_only()
    {
        var response = RustAlertEngine.Migrate(RustMigrateRequest.Node("auto_resolve", Json(
            """{"type":"not","not":{"child":{"type":"rate_of_change","rate_of_change":{"direction":"falling","rate":3}}}}""")));

        response.Outcome.Should().Be(RustMigrateOutcome.Migrated);
        var tree = response.Tree!.Value;
        tree.GetProperty("schema_version").GetInt32().Should().Be(2);
        var child = tree.GetProperty("not").GetProperty("child");
        child.TryGetProperty("schema_version", out _).Should().BeFalse();
        var rate = child.GetProperty("rate_of_change");
        rate.GetProperty("operator").GetString().Should().Be("<=");
        rate.GetProperty("value").GetDecimal().Should().Be(-3m);
    }

    [NativeFact]
    public void A_site_age_that_is_not_a_whole_minute_is_rounded_and_reported()
    {
        var response = RustAlertEngine.Migrate(
            RustMigrateRequest.Body("site_age", Json("""{"operator":">=","value":72.5083}""")));

        response.Outcome.Should().Be(RustMigrateOutcome.Migrated);
        response.Rounded.Should().BeTrue();
        response.Tree!.Value.GetProperty("minutes").GetInt32().Should().Be(4350);
    }

    [NativeFact]
    public void A_tree_already_in_schema_2_is_current()
    {
        var response = RustAlertEngine.Migrate(
            RustMigrateRequest.Body("threshold", Json("""{"schema_version":2,"operator":"<","value":70}""")));

        response.Outcome.Should().Be(RustMigrateOutcome.Current);
        response.Tree.Should().BeNull();
    }

    [NativeFact]
    public void A_tree_that_cannot_be_read_fails_with_its_issue()
    {
        var response = RustAlertEngine.Migrate(
            RustMigrateRequest.Body("threshold", Json("""{"direction":"sideways","value":70}""")));

        response.Outcome.Should().Be(RustMigrateOutcome.Failed);
        response.Issue.Should().Be(new RustMigrateIssue("threshold", "unknown_value", "direction"));
    }

    [NativeFact]
    public void Both_or_neither_of_condition_type_and_root_is_rejected()
    {
        var tree = Json("""{"direction":"below","value":70}""");

        var both = () => RustAlertEngine.Migrate(new RustMigrateRequest { Tree = tree, ConditionType = "threshold", Root = "snooze" });
        var neither = () => RustAlertEngine.Migrate(new RustMigrateRequest { Tree = tree });

        both.Should().Throw<RustAlertEngineException>();
        neither.Should().Throw<RustAlertEngineException>();
    }

    [Theory]
    [InlineData("""{"schema_version":1,"ok":true}""", "outcome")]
    [InlineData("""{"schema_version":1,"ok":true,"outcome":"migrated"}""", "tree")]
    [InlineData("""{"schema_version":1,"ok":true,"outcome":"failed"}""", "issue")]
    public void A_migrate_response_missing_a_required_field_throws(string json, string missing)
    {
        var parse = () => RustAlertEngine.ParseMigrateResponse(json);

        parse.Should().Throw<RustAlertEngineException>().WithMessage($"*'{missing}'*");
    }

    [Fact]
    public void An_unknown_outcome_throws()
    {
        var parse = () => RustAlertEngine.ParseMigrateResponse("""{"schema_version":1,"ok":true,"outcome":"partial"}""");

        parse.Should().Throw<RustAlertEngineException>();
    }
}
