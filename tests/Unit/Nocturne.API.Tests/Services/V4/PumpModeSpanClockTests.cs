using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The times a fold reads the spans around a transition (<see cref="PumpModeSpanClock.Transition.ReadAt"/>):
/// its own, and its arrival only when it was dated beyond the clock bound as it arrived. The order the source
/// sent a transition and a span's opener in decides the end, when both were sent no later than they arrived.
/// </summary>
[Trait("Category", "Unit")]
public class PumpModeSpanClockTests
{
    private static readonly DateTime Arrived = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ATransitionWithNoArrival_ReadsAtItsOwnTime()
    {
        var at = Arrived.AddDays(1);

        new PumpModeSpanClock.Transition(at, null).ReadAt.Should().Equal(at);
    }

    [Fact]
    public void ATransitionTheStoreHasNotStamped_ReadsAtItsOwnTime()
    {
        var at = Arrived.AddDays(1);

        new PumpModeSpanClock.Transition(at, default(DateTime)).ReadAt.Should().Equal(at);
    }

    [Fact]
    public void ATransitionDatedAtTheClockBound_ReadsAtItsOwnTime()
    {
        var at = AlertReadings.Bound(Arrived);

        new PumpModeSpanClock.Transition(at, Arrived).ReadAt.Should().Equal(at);
    }

    [Fact]
    public void ATransitionDatedAMillisecondBeyondTheClockBound_AlsoReadsAtItsArrival()
    {
        var at = AlertReadings.Bound(Arrived).AddMilliseconds(1);

        new PumpModeSpanClock.Transition(at, Arrived).ReadAt.Should().Equal(at, Arrived);
    }

    [Fact]
    public void ATransitionDatedAheadArrivingBelowTheMillisecond_ReadsAtItsArrivalAsTheStoreKeepsIt()
    {
        var arrived = Arrived.AddTicks(1_234_567);
        var at = Arrived.AddDays(1);

        new PumpModeSpanClock.Transition(at, arrived).ReadAt.Should().Equal(at, RecordTime.AtMillisecond(arrived));
    }

    [Fact]
    public void ATransitionThatArrivedAfterItsTime_ReadsAtItsOwnTime()
    {
        var at = Arrived.AddHours(-3);

        new PumpModeSpanClock.Transition(at, Arrived).ReadAt.Should().Equal(at);
    }

    [Fact]
    public void ATransitionSentAfterTheSpansOpener_DatedBeforeTheSpan_EndsItWhenItsSourceDatedIt()
    {
        var opener = new PumpModeSpanClock.Transition(Arrived, Arrived, Arrived);
        var sent = Arrived.AddMinutes(30).AddTicks(1_234_567);
        var transition = new PumpModeSpanClock.Transition(Arrived.AddMinutes(-30), Arrived.AddMinutes(40), sent);

        PumpModeSpanClock.EndAt(Span(Arrived, Arrived), transition, opener).Should().Be(RecordTime.AtMillisecond(sent));
        PumpModeSpanClock.EndAt(Span(Arrived, Arrived), transition).Should().BeNull();
    }

    [Fact]
    public void ATransitionSentBeforeTheSpansOpener_DatedAfterTheSpanArrived_EndsNothing()
    {
        var fast = TimeSpan.FromHours(1);
        var opener = new PumpModeSpanClock.Transition(Arrived + fast, Arrived, Arrived);
        var old = Arrived.AddMinutes(-30);
        var transition = new PumpModeSpanClock.Transition(old + fast, Arrived.AddHours(3), old);

        PumpModeSpanClock.EndAt(Span(Arrived + fast, Arrived), transition, opener).Should().BeNull();
        PumpModeSpanClock.EndAt(Span(Arrived + fast, Arrived), transition).Should().Be(Arrived + fast);
    }

    [Fact]
    public void AnOpenerSentAheadOfItsArrival_GivesNoOrder()
    {
        var ahead = Arrived.AddDays(1);
        var opener = new PumpModeSpanClock.Transition(ahead, Arrived.AddMinutes(-30), ahead);
        var transition = new PumpModeSpanClock.Transition(Arrived, Arrived.AddMinutes(30), Arrived);

        PumpModeSpanClock.EndAt(Span(ahead, Arrived.AddMinutes(-30)), transition, opener).Should().Be(ahead);
    }

    private static StateSpan Span(DateTime start, DateTime arrived) => new()
    {
        Category = StateSpanCategory.PumpMode,
        State = PumpModeState.Suspended.ToString(),
        StartTimestamp = start,
        CreatedAt = arrived,
    };
}
