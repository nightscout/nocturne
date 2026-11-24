using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

public class FullWalkScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    [Fact]
    public void IsDue_WalksWhenNoWalkHasEverCompleted()
    {
        FullWalkSchedule.IsDue(null, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void IsDue_SkipsTheWalkUntilTheIntervalHasElapsed()
    {
        var justNow = Now.AddMinutes(-30).UtcDateTime;
        var onTheInterval = (Now - Interval).UtcDateTime;
        var oneMinuteShort = (Now - Interval + TimeSpan.FromMinutes(1)).UtcDateTime;

        FullWalkSchedule.IsDue(justNow, Interval, Now).Should().BeFalse();
        FullWalkSchedule.IsDue(oneMinuteShort, Interval, Now).Should().BeFalse();
        FullWalkSchedule.IsDue(onTheInterval, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void IsDue_WalksWhenTheStoredTimeIsInTheFuture()
    {
        FullWalkSchedule.IsDue(Now.AddDays(3).UtcDateTime, Interval, Now).Should().BeTrue();
    }

    [Fact]
    public void IsDue_ReadsAStoredTimeAsUtcWhateverItsKind()
    {
        var unspecified = DateTime.SpecifyKind(Now.AddMinutes(-30).UtcDateTime, DateTimeKind.Unspecified);
        var tenHoursAhead = new DateTimeOffset(2026, 9, 9, 22, 0, 0, TimeSpan.FromHours(10));

        FullWalkSchedule.IsDue(unspecified, Interval, tenHoursAhead).Should().BeFalse(
            "11:30 stored without a kind is 11:30Z, half an hour before 22:00+10:00");
    }
}
