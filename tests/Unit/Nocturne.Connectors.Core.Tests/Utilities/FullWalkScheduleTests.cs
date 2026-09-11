using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

public class FullWalkScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a timestamp")]
    public void IsDue_WalksWhenNoWalkHasEverCompleted(string? stamp)
    {
        FullWalkSchedule.IsDue(stamp, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void IsDue_SkipsTheWalkUntilTheIntervalHasElapsed()
    {
        var justNow = FullWalkSchedule.Stamp(Now.AddMinutes(-30));
        var onTheInterval = FullWalkSchedule.Stamp(Now - Interval);
        var oneMinuteShort = FullWalkSchedule.Stamp(Now - Interval + TimeSpan.FromMinutes(1));

        FullWalkSchedule.IsDue(justNow, Interval, Now).Should().BeFalse();
        FullWalkSchedule.IsDue(oneMinuteShort, Interval, Now).Should().BeFalse();
        FullWalkSchedule.IsDue(onTheInterval, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void IsDue_WalksWhenTheStoredTimeIsInTheFuture()
    {
        var ahead = FullWalkSchedule.Stamp(Now.AddDays(3));

        FullWalkSchedule.IsDue(ahead, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void Stamp_RoundTripsThroughIsDueWithTheInstantIntact()
    {
        // A stamp in another zone still compares as the instant it names.
        var completedAt = new DateTimeOffset(2026, 9, 9, 22, 0, 0, TimeSpan.FromHours(10));
        var stamp = FullWalkSchedule.Stamp(completedAt);

        stamp.Should().Be("2026-09-09T22:00:00.0000000+10:00");
        FullWalkSchedule.IsDue(stamp, Interval, Now).Should().BeFalse("12:00Z is the same instant, not a day later");
        FullWalkSchedule.IsDue(stamp, Interval, Now.AddDays(1)).Should().BeTrue();
    }
}
