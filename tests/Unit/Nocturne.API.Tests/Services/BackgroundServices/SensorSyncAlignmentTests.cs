using FluentAssertions;
using Nocturne.API.Services.BackgroundServices;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class SensorSyncAlignmentTests
{
    private static readonly DateTime Reading = new(2026, 9, 25, 17, 4, 29, DateTimeKind.Utc);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void BeforeTheNextReadingIsDue_PollsJustAfterItShouldReachTheCloud()
    {
        var next = SensorSyncAlignment.NextSyncAt(Reading, FiveMinutes, Reading.AddMinutes(2), jitterFraction: 0);

        next.Should().Be(Reading + FiveMinutes + SensorSyncAlignment.PublishBuffer);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Jitter_StaysInsideItsBound(double fraction)
    {
        var onTime = Reading + FiveMinutes + SensorSyncAlignment.PublishBuffer;

        var next = SensorSyncAlignment.NextSyncAt(Reading, FiveMinutes, Reading.AddMinutes(1), fraction);

        next.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + SensorSyncAlignment.JitterFor(FiveMinutes));
    }

    /// <summary>
    /// The reading is due but the cloud does not have it yet, usually because the phone has not
    /// uploaded it. That is when a fixed timer loses a whole interval, so ask again soon.
    /// </summary>
    [Fact]
    public void WhenTheExpectedReadingIsLate_RetriesShortly()
    {
        var now = Reading + FiveMinutes + TimeSpan.FromSeconds(45);

        var next = SensorSyncAlignment.NextSyncAt(Reading, FiveMinutes, now, jitterFraction: 0);

        next.Should().Be(now + SensorSyncAlignment.LateRetryIntervalFor(FiveMinutes));
    }

    /// <summary>
    /// Past the retry window the gap is real (signal loss, warm-up), so polling returns to the
    /// reading grid instead of retrying until the sensor comes back.
    /// </summary>
    [Fact]
    public void PastTheRetryWindow_ReturnsToTheReadingGrid()
    {
        var now = Reading + TimeSpan.FromMinutes(12);

        var next = SensorSyncAlignment.NextSyncAt(Reading, FiveMinutes, now, jitterFraction: 0);

        next.Should().Be(Reading + TimeSpan.FromMinutes(15) + SensorSyncAlignment.PublishBuffer);
    }

    [Fact]
    public void AReadingOlderThanTheAlignableAge_LeavesTheIntervalInCharge()
    {
        var now = Reading + SensorSyncAlignment.MaxAlignableAge + TimeSpan.FromMinutes(1);

        SensorSyncAlignment.NextSyncAt(Reading, FiveMinutes, now, jitterFraction: 0).Should().BeNull();
    }

    /// <summary>
    /// At a five-minute cadence the ceilings apply; a one-minute sensor is never retried more often
    /// than half its cadence nor for longer than most of one.
    /// </summary>
    [Fact]
    public void Allowances_ScaleDownWithAShortCadence_AndCapAtTheirCeilings()
    {
        SensorSyncAlignment.JitterFor(FiveMinutes).Should().Be(SensorSyncAlignment.MaxJitter);
        SensorSyncAlignment.LateRetryIntervalFor(FiveMinutes).Should().Be(SensorSyncAlignment.LateRetryInterval);
        SensorSyncAlignment.LateRetryWindowFor(FiveMinutes).Should().Be(SensorSyncAlignment.LateRetryWindow);

        SensorSyncAlignment.JitterFor(OneMinute).Should().Be(TimeSpan.FromSeconds(6));
        SensorSyncAlignment.LateRetryIntervalFor(OneMinute).Should().Be(TimeSpan.FromSeconds(30));
        SensorSyncAlignment.LateRetryWindowFor(OneMinute).Should().Be(TimeSpan.FromSeconds(36));
    }

    [Fact]
    public void AOneMinuteSensor_IsAskedJustAfterItsNextReading()
    {
        var next = SensorSyncAlignment.NextSyncAt(Reading, OneMinute, Reading.AddSeconds(30), jitterFraction: 0);

        next.Should().Be(Reading + OneMinute + SensorSyncAlignment.PublishBuffer);
    }

    [Fact]
    public void ANonPositiveCadence_IsRejected()
    {
        var act = () => SensorSyncAlignment.NextSyncAt(Reading, TimeSpan.Zero, Reading, jitterFraction: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Every answer is in the future, across a sweep of the cadence, the retry window and a long gap,
    /// so the poller can never be pointed at a time that has already passed.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    public void EveryAnswer_IsInTheFuture(int cadenceSeconds)
    {
        var cadence = TimeSpan.FromSeconds(cadenceSeconds);

        for (var seconds = 0; seconds <= 3600; seconds += 7)
        {
            var now = Reading.AddSeconds(seconds);
            var next = SensorSyncAlignment.NextSyncAt(Reading, cadence, now, jitterFraction: 0);

            if (next is { } at)
                at.Should().BeAfter(now, "at {0}s after the reading", seconds);
        }
    }
}
