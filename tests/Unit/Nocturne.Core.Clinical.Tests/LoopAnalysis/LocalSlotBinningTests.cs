using Nocturne.Core.Clinical.LoopAnalysis;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class LocalSlotBinningTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTime Utc(int y, int m, int d, int h, int min = 0, int s = 0, int ms = 0) =>
        new(y, m, d, h, min, s, ms, DateTimeKind.Utc);

    [Fact]
    public void LocalDays_CountsDaysOnTheZonesCalendarNotUtcs()
    {
        var days = LocalSlotBinning.LocalDays(Utc(2026, 1, 9, 13), Utc(2026, 1, 12, 12, 59, 59, 999), Sydney);

        days.Should().Equal(new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), new DateOnly(2026, 1, 12));
    }

    [Fact]
    public void LocalDays_SplitsAtLocalMidnightToTheMillisecond()
    {
        LocalSlotBinning.LocalDays(Utc(2026, 1, 9, 12, 59, 59, 999), Utc(2026, 1, 9, 13), Sydney)
            .Should().Equal(new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 10));
        LocalSlotBinning.LocalDays(Utc(2026, 1, 9, 13), Utc(2026, 1, 10, 12, 59, 59, 999), Sydney)
            .Should().Equal(new DateOnly(2026, 1, 10));
    }

    [Fact]
    public void LocalDays_HasNoGapOrRepeatAcrossAFallBack()
    {
        // Sydney leaves daylight time on 7 Apr 2024, so that local day lasts 25 hours.
        var days = LocalSlotBinning.LocalDays(Utc(2024, 4, 5, 13), Utc(2024, 4, 8, 13, 59, 59, 999), Sydney);

        days.Should().Equal(new DateOnly(2024, 4, 6), new DateOnly(2024, 4, 7), new DateOnly(2024, 4, 8));
    }

    [Fact]
    public void LocalDays_HasNoGapOrRepeatAcrossASpringForward()
    {
        // Sydney enters daylight time on 6 Oct 2024, so that local day lasts 23 hours.
        var days = LocalSlotBinning.LocalDays(Utc(2024, 10, 4, 14), Utc(2024, 10, 7, 12, 59, 59, 999), Sydney);

        days.Should().Equal(new DateOnly(2024, 10, 5), new DateOnly(2024, 10, 6), new DateOnly(2024, 10, 7));
    }

    [Fact]
    public void LocalDays_IsEmptyWhenToIsOnAnEarlierDay()
    {
        LocalSlotBinning.LocalDays(Utc(2026, 1, 12, 0), Utc(2026, 1, 10, 0), TimeZoneInfo.Utc).Should().BeEmpty();
    }

    [Fact]
    public void LocalDaySpan_ReadsAnUnspecifiedKindBoundAsUtc()
    {
        var (first, last) = LocalSlotBinning.LocalDaySpan(
            new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 1, 12, 12, 59, 59, 999, DateTimeKind.Unspecified),
            Sydney);

        (first, last).Should().Be((new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12)));
    }
}
