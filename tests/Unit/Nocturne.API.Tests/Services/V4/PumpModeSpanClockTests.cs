using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The times a fold reads the spans around a transition (<see cref="PumpModeSpanClock.Transition.ReadAt"/>):
/// its own, and its arrival only when it was dated beyond the clock bound as it arrived.
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
}
