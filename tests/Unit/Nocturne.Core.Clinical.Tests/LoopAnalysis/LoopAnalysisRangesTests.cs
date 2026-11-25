using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.LoopAnalysis;

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

    private static LoopAnalysisResolvedRange Intersected(DateOnly start, DateOnly end, DateTime now) =>
        LoopAnalysisRanges.Intersect(
            LoopAnalysisRanges.Resolve(start, end, Sydney, 90, now), now - TimeSpan.FromHours(24), now, Sydney);

    [Fact]
    public void Intersect_ARangeInsideTheWindow_IsUnchanged()
    {
        var range = Intersected(new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 10), SixTwenty);

        range.From.Should().Be(Utc(2024, 6, 9, 14));
        range.To.Should().Be(SixTwenty);
        range.StartDate.Should().Be(new DateOnly(2024, 6, 10));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
    }

    [Fact]
    public void Intersect_AWindowStartingMidDay_StartsTheRangeThere_OnThatLocalDay()
    {
        var range = Intersected(new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 10), SixTwenty);

        range.From.Should().Be(Utc(2024, 6, 8, 20, 20), "06:20 on 9 June, local");
        range.To.Should().Be(SixTwenty);
        range.StartDate.Should().Be(new DateOnly(2024, 6, 9));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
    }

    [Fact]
    public void Intersect_ARangeBeforeTheWindow_IsEmptyAtTheWindowStart()
    {
        var range = Intersected(new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 3), SixTwenty);

        range.From.Should().Be(Utc(2024, 6, 8, 20, 20));
        range.To.Should().Be(range.From);
        range.StartDate.Should().Be(new DateOnly(2024, 6, 9));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 9));
    }

    [Fact]
    public void Intersect_ARangePastTheWindowEnd_EndsThere_AndOneWhollyAfterItIsEmpty()
    {
        var day = new LoopAnalysisResolvedRange
        {
            StartDate = new DateOnly(2024, 6, 10), EndDate = new DateOnly(2024, 6, 10),
            From = Utc(2024, 6, 9, 14), To = Utc(2024, 6, 10, 14),
        };

        var capped = LoopAnalysisRanges.Intersect(day, Utc(2024, 6, 8, 20, 20), SixTwenty, Sydney);
        capped.From.Should().Be(Utc(2024, 6, 9, 14));
        capped.To.Should().Be(SixTwenty);
        capped.EndDate.Should().Be(new DateOnly(2024, 6, 10));

        var after = LoopAnalysisRanges.Intersect(day, Utc(2024, 6, 8, 0), Utc(2024, 6, 9, 0), Sydney);
        after.From.Should().Be(Utc(2024, 6, 8, 0));
        after.To.Should().Be(after.From);
    }

    [Fact]
    public void Intersect_OnTheDayClocksGoBack_KeepsTheWindowLength_AndItsLocalDay()
    {
        // 2024-04-07 in Sydney is 25 hours long: 03:00 AEDT falls back to 02:00 AEST. Now is 23:30 AEST.
        var now = Utc(2024, 4, 7, 13, 30);

        var range = Intersected(new DateOnly(2024, 4, 6), new DateOnly(2024, 4, 7), now);

        range.From.Should().Be(Utc(2024, 4, 6, 13, 30), "00:30 AEDT on 7 April");
        range.To.Should().Be(now);
        (range.To - range.From).Should().Be(TimeSpan.FromHours(24));
        range.StartDate.Should().Be(new DateOnly(2024, 4, 7));
        range.EndDate.Should().Be(new DateOnly(2024, 4, 7));
    }

    [Fact]
    public void Intersect_AWindowStartingOnLocalMidnight_SpansOneWholeLocalDay()
    {
        var now = Utc(2024, 6, 10, 14); // midnight on 11 June, local

        var range = Intersected(new DateOnly(2024, 6, 1), new DateOnly(2024, 6, 11), now);

        range.From.Should().Be(Utc(2024, 6, 9, 14), "midnight on 10 June, local");
        range.To.Should().Be(now);
        range.StartDate.Should().Be(new DateOnly(2024, 6, 10));
        range.EndDate.Should().Be(new DateOnly(2024, 6, 10));
    }

    [Fact]
    public void Intersect_OnTheDayClocksGoForward_TakesAnHourOfTheDayBefore()
    {
        // 2024-10-06 in Sydney is 23 hours long: 02:00 AEST springs forward to 03:00 AEDT. Now is midnight on 7 October.
        var now = Utc(2024, 10, 6, 13);

        var range = Intersected(new DateOnly(2024, 10, 5), new DateOnly(2024, 10, 7), now);

        range.From.Should().Be(Utc(2024, 10, 5, 13), "23:00 AEST on 5 October");
        range.To.Should().Be(now);
        (range.To - range.From).Should().Be(TimeSpan.FromHours(24));
        range.StartDate.Should().Be(new DateOnly(2024, 10, 5));
        range.EndDate.Should().Be(new DateOnly(2024, 10, 6));
    }

    [Fact]
    public void Intersect_KeepsShortenedOnlyWhenTheStartWasNotMoved()
    {
        var shortened = LoopAnalysisRanges.Resolve(new DateOnly(2024, 1, 1), new DateOnly(2024, 6, 10), Sydney, 90, SixTwenty);
        shortened.Shortened.Should().BeTrue();

        LoopAnalysisRanges.Intersect(shortened, SixTwenty.AddHours(-24), SixTwenty, Sydney).Shortened
            .Should().BeFalse("the window, not the 90-day limit, set the start");
        LoopAnalysisRanges.Intersect(shortened, shortened.From.AddDays(-1), SixTwenty, Sydney).Shortened
            .Should().BeTrue();
        LoopAnalysisRanges.Intersect(shortened, SixTwenty.AddDays(1), SixTwenty.AddDays(2), Sydney).Shortened
            .Should().BeFalse("an empty range kept nothing");
    }
}
