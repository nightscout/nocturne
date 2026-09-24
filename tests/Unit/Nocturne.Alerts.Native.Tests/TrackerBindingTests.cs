using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>The tracker entry points: response strictness, and round trips through the library.</summary>
public class TrackerBindingTests
{
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly RustTrackerState Active = new()
    {
        State = "active",
        ActiveExcursionOrdinal = 1,
        UpdatedAt = Now.AddMinutes(-5),
        NextExcursionOrdinal = 2,
    };

    private const string PostTracker = """{"state":"idle","confirmation_count":0,"updated_at":"2026-01-05T12:00:00Z","next_excursion_ordinal":2}""";

    private static string Response(string transition, string tracker = PostTracker, int schemaVersion = 1) =>
        $$"""{"schema_version":{{schemaVersion}},"ok":true,"transition":{{transition}},"tracker":{{tracker}}}""";

    [Fact]
    public void A_complete_close_parses()
    {
        var response = RustAlertEngine.ParseTrackerResponse(
            Response("""{"type":"closed","excursion_ordinal":1,"close_reason":"manual"}"""), "tracker_force_close");

        response.Transition!.Type.Should().Be(RustTransition.Closed);
        response.Transition.ExcursionOrdinal.Should().Be(1);
        response.Transition.CloseReason.Should().Be(RustCloseReason.Manual);
        response.Tracker!.State.Should().Be("idle");
    }

    [Theory]
    [InlineData("""{"schema_version":1,"ok":true,"tracker":{}}""", "transition")]
    [InlineData("""{"schema_version":1,"ok":true,"transition":{"type":"none"}}""", "tracker")]
    [InlineData("""{"schema_version":1,"ok":true,"transition":{"type":"closed"},"tracker":{}}""", "transition.close_reason")]
    [InlineData("""{"schema_version":1,"ok":true,"transition":{"type":"opened","close_reason":"auto"},"tracker":{}}""", "transition.close_reason")]
    [InlineData("""{"schema_version":1,"ok":true,"transition":{"type":"none"},"tracker":{"state":"idle"}}""", "tracker.updated_at")]
    public void A_response_missing_a_required_field_throws(string json, string missing)
    {
        var act = () => RustAlertEngine.ParseTrackerResponse(json, "tracker_process");

        act.Should().Throw<RustAlertEngineException>().WithMessage($"*tracker_process*'{missing}'*");
    }

    [Theory]
    [InlineData("""{"close_reason":"auto"}""")]
    [InlineData("""{"type":"sideways"}""")]
    [InlineData("""{"type":"closed","close_reason":"bored"}""")]
    public void A_transition_without_a_known_type_or_reason_throws(string transition)
    {
        var act = () => RustAlertEngine.ParseTrackerResponse(Response(transition), "tracker_process");

        act.Should().Throw<RustAlertEngineException>();
    }

    [Fact]
    public void Another_schema_version_throws()
    {
        var act = () => RustAlertEngine.ParseTrackerResponse(Response("""{"type":"none"}""", schemaVersion: 2), "tracker_process");

        act.Should().Throw<RustAlertEngineException>().WithMessage("*schema_version 2*");
    }

    [Fact]
    public void An_error_envelope_names_the_operation_but_not_the_request()
    {
        var act = () => RustAlertEngine.ParseTrackerResponse(
            """{"schema_version":1,"ok":false,"error":"unknown close reason"}""", "tracker_force_close");

        act.Should().Throw<RustAlertEngineException>()
            .WithMessage("*tracker_force_close*unknown close reason*");
    }

    [NativeFact]
    public void Process_opens_an_excursion_from_no_state()
    {
        var response = RustAlertEngine.TrackerProcess(new RustTrackerProcessRequest
        {
            Now = Now,
            Config = new RustTrackerConfig(),
            ConditionMet = true,
        });

        response.Transition!.Type.Should().Be(RustTransition.Opened);
        response.Tracker!.State.Should().Be("active");
        response.Tracker.ActiveExcursionOrdinal.Should().Be(response.Transition.ExcursionOrdinal);
        response.Tracker.UpdatedAt.Should().Be(Now);
    }

    [NativeFact]
    public void Process_starts_hysteresis_on_a_false_evaluation()
    {
        var response = RustAlertEngine.TrackerProcess(new RustTrackerProcessRequest
        {
            Tracker = Active,
            Now = Now,
            Config = new RustTrackerConfig { HysteresisMinutes = 10 },
            ConditionMet = false,
        });

        response.Transition!.Type.Should().Be(RustTransition.HysteresisStarted);
        response.Tracker!.HysteresisStartedAt.Should().Be(Now);
    }

    [NativeFact]
    public void Force_close_closes_with_the_requested_reason()
    {
        var response = RustAlertEngine.TrackerForceClose(new RustTrackerForceCloseRequest
        {
            Tracker = Active,
            Now = Now,
            Reason = RustCloseReason.Manual,
        });

        response.Transition!.Type.Should().Be(RustTransition.Closed);
        response.Transition.CloseReason.Should().Be(RustCloseReason.Manual);
        response.Transition.ExcursionOrdinal.Should().Be(1);
        response.Tracker!.State.Should().Be("idle");
        response.Tracker.ActiveExcursionOrdinal.Should().BeNull();
    }

    [NativeFact]
    public void Close_elapsed_hysteresis_waits_for_the_window()
    {
        var inHysteresis = Active with { State = "hysteresis", HysteresisStartedAt = Now.AddMinutes(-5) };

        var early = RustAlertEngine.TrackerCloseElapsedHysteresis(new RustTrackerCloseElapsedHysteresisRequest
        {
            Tracker = inHysteresis,
            Now = Now,
            Config = new RustTrackerConfig { HysteresisMinutes = 10 },
        });
        var elapsed = RustAlertEngine.TrackerCloseElapsedHysteresis(new RustTrackerCloseElapsedHysteresisRequest
        {
            Tracker = inHysteresis,
            Now = Now.AddMinutes(5),
            Config = new RustTrackerConfig { HysteresisMinutes = 10 },
        });

        early.Transition!.Type.Should().Be(RustTransition.None);
        early.Tracker!.State.Should().Be("hysteresis");
        elapsed.Transition!.Type.Should().Be(RustTransition.Closed);
        elapsed.Transition.CloseReason.Should().Be(RustCloseReason.Hysteresis);
    }

    [NativeFact]
    public void Close_elapsed_hysteresis_adopts_updated_at_as_a_missing_start()
    {
        var response = RustAlertEngine.TrackerCloseElapsedHysteresis(new RustTrackerCloseElapsedHysteresisRequest
        {
            Tracker = Active with { State = "hysteresis" },
            Now = Now,
            Config = new RustTrackerConfig { HysteresisMinutes = 10 },
        });

        response.Transition!.Type.Should().Be(RustTransition.None);
        response.Tracker!.HysteresisStartedAt.Should().Be(Active.UpdatedAt);
    }

    [NativeFact]
    public void An_unknown_tracker_state_holding_an_excursion_reads_as_active()
    {
        var response = RustAlertEngine.TrackerForceClose(new RustTrackerForceCloseRequest
        {
            Tracker = Active with { State = "sideways" },
            Now = Now,
            Reason = RustCloseReason.Auto,
        });

        response.Transition!.Type.Should().Be(RustTransition.Closed);
        response.Tracker!.State.Should().Be("idle");
        response.Tracker.AwaitingRearm.Should().BeTrue();
    }
}
