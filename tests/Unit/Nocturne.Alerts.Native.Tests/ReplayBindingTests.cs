using FluentAssertions;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>A replay response missing a field its shape requires is an engine failure, never a default.</summary>
public class ReplayBindingTests
{
    private const string RuleId = "00000000-0000-0000-0000-0000000000aa";

    private static string Response(string fields) =>
        $$"""{"schema_version":1,"ok":true,{{fields}}}""";

    private const string Complete =
        $$"""
        "order":["{{RuleId}}"],
        "events":[{"at":"2026-01-05T12:00:00Z","rule_id":"{{RuleId}}","kind":"suppressed_by_dnd"}],
        "leaf_transitions":[{"rule_id":"{{RuleId}}","leaves":[{"leaf_id":0,"points":[{"at_ms":1767614400000,"value":true}]}]}]
        """;

    [Fact]
    public void A_complete_response_parses()
    {
        var response = RustAlertEngine.ParseReplayResponse(Response(Complete), ticksRequested: false);

        response.Events.Should().ContainSingle().Which.Kind.Should().Be(RustReplayEventKind.SuppressedByDnd);
        response.LeafTransitions![0].Leaves[0].Points[0].AtMs.Should().Be(1767614400000);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("events")]
    [InlineData("leaf_transitions")]
    public void A_response_missing_a_required_field_throws(string field)
    {
        var fields = string.Join(',', Complete.Split('\n').Where(l => !l.TrimStart().StartsWith($"\"{field}\"")))
            .Replace(",,", ",").Trim(',');

        var act = () => RustAlertEngine.ParseReplayResponse(Response(fields), ticksRequested: false);

        act.Should().Throw<RustAlertEngineException>().WithMessage($"*'{field}'*");
    }

    [Fact]
    public void Ticks_are_required_once_requested()
    {
        var act = () => RustAlertEngine.ParseReplayResponse(Response(Complete), ticksRequested: true);

        act.Should().Throw<RustAlertEngineException>().WithMessage("*'ticks'*");
    }

    [Fact]
    public void A_rule_tick_that_was_not_skipped_needs_its_state()
    {
        var ticks = $$""","ticks":[{"at":"2026-01-05T12:00:00Z","rules":[{"rule_id":"{{RuleId}}","met":true}]}]""";

        var act = () => RustAlertEngine.ParseReplayResponse(Response(Complete + ticks), ticksRequested: true);

        act.Should().Throw<RustAlertEngineException>().WithMessage("*'ticks.rules.met'*");
    }

    [Fact]
    public void An_unknown_event_kind_throws()
    {
        var act = () => RustAlertEngine.ParseReplayResponse(
            Response(Complete.Replace("suppressed_by_dnd", "snoozed")), ticksRequested: false);

        act.Should().Throw<RustAlertEngineException>();
    }

    /// <summary>The engine's step-limit error, as <c>ReplayError::StepLimit</c> words it, names its rule.</summary>
    [Fact]
    public void A_step_limit_rejection_names_its_rule()
    {
        var act = () => RustAlertEngine.ParseReplayResponse(
            $$"""{"schema_version":1,"ok":false,"error":"rule {{RuleId}} changes more than 1000 times between two ticks, at 2026-01-05T12:00:00Z"}""",
            ticksRequested: false);

        act.Should().Throw<RustAlertEngineException>().Which.ReplayStepLimitRule.Should().Be(Guid.Parse(RuleId));
    }

    [Fact]
    public void Another_rejection_names_no_step_limit_rule()
    {
        var act = () => RustAlertEngine.ParseReplayResponse(
            $$"""{"schema_version":1,"ok":false,"error":"rule id {{RuleId}} appears more than once"}""",
            ticksRequested: false);

        act.Should().Throw<RustAlertEngineException>().Which.ReplayStepLimitRule.Should().BeNull();
    }
}
