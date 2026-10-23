using Nocturne.Core.Clinical.LoopAnalysis;

namespace Nocturne.Core.Clinical.Tests.LoopAnalysis;

public class LoopAnalysisRangesTests
{
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static readonly DateTime Later = Utc(2030, 1, 1, 0);

    // 06:20 on 2024-06-10 in Sydney (UTC+10, no DST in June).
    private static readonly DateTime SixTwenty = Utc(2024, 6, 9, 20, 20);

    [Fact]
    public void NinetyLocalDaysAcrossFallBack_AreKeptWhole()
    {
        // 2024-04-07 in Sydney is 25 hours long, so these 90 days are 90 days and an hour.
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 3, 1), new DateOnly(2024, 5, 29), Sydney, 90, Later);

        range.Shortened.Should().BeFalse();
        range.From.Should().Be(Utc(2024, 2, 29, 13));
        range.To.Should().Be(Utc(2024, 5, 29, 14));
        (range.To - range.From).Should().Be(TimeSpan.FromDays(90) + TimeSpan.FromHours(1));
    }

    [Fact]
    public void ALongerRange_KeepsItsMostRecentDays_AndPreviousFollowsTheKeptLength()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 1, 1), new DateOnly(2024, 6, 30), Sydney, 90, Later);

        range.Shortened.Should().BeTrue();
        range.StartDate.Should().Be(new DateOnly(2024, 4, 2));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 30));

        var previous = LoopAnalysisRanges.Previous(range, Sydney);
        previous.StartDate.Should().Be(new DateOnly(2024, 1, 3));
        previous.EndDate.Should().Be(new DateOnly(2024, 4, 1));
        previous.To.Should().Be(range.From);
        previous.Shortened.Should().BeFalse();
    }

    [Fact]
    public void Clamped_IsTheLastWindowBeforeNow()
    {
        var now = Utc(2024, 6, 10, 3);

        var range = LoopAnalysisRanges.Clamped(now, TimeSpan.FromHours(24), Sydney);

        range.From.Should().Be(now.AddHours(-24));
        range.To.Should().Be(now);
        range.StartDate.Should().Be(new DateOnly(2024, 6, 9));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
        range.To.Should().BeAfter(range.From);
    }

    [Fact]
    public void ARangeThroughToday_EndsAtNow()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 6, 4), new DateOnly(2024, 6, 10), Sydney, 90, SixTwenty);

        range.From.Should().Be(Utc(2024, 6, 3, 14));
        range.To.Should().Be(SixTwenty);
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
    }

    [Fact]
    public void ARangeEndingAfterToday_EndsAtNow_AndKeepsItsMostRecentDaysUpToToday()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 3, 1), new DateOnly(2024, 6, 30), Sydney, 90, SixTwenty);

        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
        range.StartDate.Should().Be(new DateOnly(2024, 3, 13));
        range.Shortened.Should().BeTrue();
        range.To.Should().Be(SixTwenty);
    }

    [Fact]
    public void ARangeStartingAfterToday_ResolvesToTodaySoFar()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 6, 11), new DateOnly(2024, 6, 12), Sydney, 90, SixTwenty);

        range.StartDate.Should().Be(new DateOnly(2024, 6, 10));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
        range.From.Should().Be(Utc(2024, 6, 9, 14));
        range.To.Should().Be(SixTwenty);
        range.Shortened.Should().BeFalse();
    }

    [Fact]
    public void ARangeStartingAtTheInstantItsDayBegins_ResolvesToTheDayBefore()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 10), Sydney, 90, Utc(2024, 6, 9, 14));

        range.StartDate.Should().Be(new DateOnly(2024, 6, 9));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 9));
        range.To.Should().Be(Utc(2024, 6, 9, 14));
        range.To.Should().BeAfter(range.From);
    }

    [Fact]
    public void PreviousOfAPartialToday_EndsTheSameTimeIntoItsLastDay_AndIsAsLong()
    {
        var range = LoopAnalysisRanges.Resolve(new DateOnly(2024, 6, 4), new DateOnly(2024, 6, 10), Sydney, 90, SixTwenty);

        var previous = LoopAnalysisRanges.Previous(range, Sydney);

        previous.StartDate.Should().Be(new DateOnly(2024, 5, 28));
        previous.EndDate.Should().Be(new DateOnly(2024, 6, 3));
        previous.From.Should().Be(Utc(2024, 5, 27, 14));
        previous.To.Should().Be(Utc(2024, 6, 2, 20, 20), "06:20 on 3 June, local");
        (previous.To - previous.From).Should().Be(range.To - range.From);
    }
}
