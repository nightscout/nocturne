using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// A response missing a field its shape requires, carrying a value this binding does not know,
/// or of another schema version is an engine failure, never a default.
/// </summary>
public class ResponseStrictnessTests
{
    private const string RuleId = "00000000-0000-0000-0000-0000000000aa";

    private const string Tracker = """{"state":"active","active_excursion_ordinal":1,"updated_at":"2026-01-05T12:00:00Z","next_excursion_ordinal":2}""";

    private static string Evaluate(string result, string tracker = Tracker, int schemaVersion = 1) =>
        $$"""{"schema_version":{{schemaVersion}},"ok":true,"result":{{result}},"timers":{},"not_true":{},"tracker":{{tracker}}}""";

    private static RustRuleResult RuleResult(string result) =>
        RustAlertEngine.GetRuleResult(RustAlertEngine.ParseEvaluateResponse(Evaluate(result)));

    [Fact]
    public void A_complete_result_parses()
    {
        var result = RuleResult($$"""{"rule_id":"{{RuleId}}","root":true,"leaves":[],"transition":"closed","close_reason":"auto","auto_resolved":true}""");

        result.Transition.Should().Be(RustTransition.Closed);
        result.CloseReason.Should().Be(RustCloseReason.Auto);
        result.AutoResolved.Should().BeTrue();
    }

    [Fact]
    public void A_skipped_result_needs_no_outcome_fields()
    {
        RuleResult($$"""{"rule_id":"{{RuleId}}","skipped":true}""").Skipped.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"leaves":[],"transition":"none"}""", "result.root")]
    [InlineData("""{"root":true,"transition":"none"}""", "result.leaves")]
    [InlineData("""{"root":true,"leaves":[]}""", "result.transition")]
    [InlineData("""{"root":false,"leaves":[],"transition":"closed"}""", "result.close_reason")]
    [InlineData("""{"root":true,"leaves":[],"transition":"opened","close_reason":"auto"}""", "result.close_reason")]
    public void A_result_missing_a_required_field_throws(string fields, string missing)
    {
        var result = fields.Insert(1, $"\"rule_id\":\"{RuleId}\",");

        var act = () => RuleResult(result);

        act.Should().Throw<RustAlertEngineException>().WithMessage($"*'{missing}'*");
    }

    [Fact]
    public void Leaves_are_optional_when_they_were_not_requested()
    {
        var response = RustAlertEngine.ParseEvaluateResponse(
            Evaluate($$"""{"rule_id":"{{RuleId}}","root":true,"transition":"none"}"""));

        RustAlertEngine.GetRuleResult(response, leavesRequested: false).Leaves.Should().BeNull();
    }

    [Theory]
    [InlineData("\"sideways\"")]
    [InlineData("3")]
    [InlineData("null")]
    public void An_unknown_transition_throws(string transition)
    {
        var act = () => RuleResult($$"""{"rule_id":"{{RuleId}}","root":true,"leaves":[],"transition":{{transition}}}""");

        act.Should().Throw<RustAlertEngineException>();
    }

    [Theory]
    [InlineData("""[{"op":"set","path":"sustained"}]""")]
    [InlineData("""[{"op":"clear","path":"sustained"}]""")]
    [InlineData("""[{"op":"reset","path":"sustained","at":"2026-01-05T12:00:00Z"}]""")]
    [InlineData("""[{"op":"clear","at":"2026-01-05T12:00:00Z"}]""")]
    public void A_malformed_timer_op_throws(string ops)
    {
        var act = () => RustAlertEngine.ParseEvaluateNodeResponse(
            $$"""{"schema_version":1,"ok":true,"value":true,"timers":{},"not_true":{},"timer_ops":{{ops}}}""");

        act.Should().Throw<RustAlertEngineException>();
    }

    [Fact]
    public void Timer_ops_parse()
    {
        var response = RustAlertEngine.ParseEvaluateNodeResponse(
            """{"schema_version":1,"ok":true,"value":true,"timers":{},"not_true":{},"timer_ops":[{"op":"set","path":"a","at":"2026-01-05T12:00:00Z"},{"op":"clear","path":"b","at":"2026-01-05T12:05:00Z"}]}""");

        response.TimerOps!.Select(o => (o.Op, o.At)).Should().Equal(
            (RustTimerOpKind.Set, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc)),
            (RustTimerOpKind.Clear, new DateTime(2026, 1, 5, 12, 5, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Another_schema_version_throws()
    {
        var act = () => RustAlertEngine.ParseEvaluateResponse(
            Evaluate($$"""{"rule_id":"{{RuleId}}","skipped":true}""", schemaVersion: 2));

        act.Should().Throw<RustAlertEngineException>().WithMessage("*schema_version 2*");
    }

    [Theory]
    [InlineData("""{"ok":true,"result":{},"timers":{},"tracker":{}}""")]
    [InlineData("""{"schema_version":1,"result":{},"timers":{},"tracker":{}}""")]
    public void An_envelope_without_schema_version_or_ok_throws(string json)
    {
        var act = () => RustAlertEngine.ParseEvaluateResponse(json);

        act.Should().Throw<RustAlertEngineException>().WithInnerException<JsonException>();
    }

    [Fact]
    public void A_tracker_state_without_updated_at_throws()
    {
        var act = () => RustAlertEngine.ParseEvaluateResponse(Evaluate(
            $$"""{"rule_id":"{{RuleId}}","skipped":true}""",
            tracker: """{"state":"idle","next_excursion_ordinal":1}"""));

        act.Should().Throw<RustAlertEngineException>().WithMessage("*'tracker.updated_at'*");
    }

    [Theory]
    [InlineData("""{"schema_version":1,"ok":true,"timers":{},"not_true":{},"timer_ops":[]}""", "value")]
    [InlineData("""{"schema_version":1,"ok":true,"value":true,"not_true":{},"timer_ops":[]}""", "timers")]
    [InlineData("""{"schema_version":1,"ok":true,"value":true,"timers":{},"timer_ops":[]}""", "not_true")]
    public void An_evaluate_node_response_missing_a_required_field_throws(string json, string missing)
    {
        var act = () => RustAlertEngine.ParseEvaluateNodeResponse(json);

        act.Should().Throw<RustAlertEngineException>().WithMessage($"*'{missing}'*");
    }

    [Fact]
    public void An_unparseable_response_does_not_echo_its_body()
    {
        const string body = """{"schema_version":1,"ok":true,"value":"187 mg/dL","timers":{}}""";

        var act = () => RustAlertEngine.ParseEvaluateNodeResponse(body);

        act.Should().Throw<RustAlertEngineException>()
            .Which.Message.Should().Contain("$.value").And.NotContain("187");
    }

    [Fact]
    public void An_unparseable_result_does_not_echo_its_body()
    {
        var act = () => RuleResult($$"""{"rule_id":"{{RuleId}}","root":"187","leaves":[],"transition":"none"}""");

        act.Should().Throw<RustAlertEngineException>()
            .Which.Message.Should().Contain("$.root").And.NotContain("187");
    }
}
