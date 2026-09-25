using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class HourlyPatternsTests
{
    private readonly StatisticsService _service = new();

    private static readonly DateTime FirstDay = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Five-minute readings at <paramref name="hour"/> UTC on each of <paramref name="days"/>
    /// consecutive days, cycling through <paramref name="values"/> within each day.
    /// </summary>
    private static IEnumerable<SensorGlucose> Hour(int hour, int days, params double[] values) =>
        Enumerable.Range(0, days).SelectMany(day =>
            values.Select((mgdl, i) => new SensorGlucose
            {
                Mgdl = mgdl,
                Timestamp = FirstDay.AddDays(day).AddHours(hour).AddMinutes(i * 5),
            }));

    /// <summary>A rankable hour: twelve readings a day on seven days, <paramref name="inRange"/> of the twelve at 120.</summary>
    private static IEnumerable<SensorGlucose> RankableHour(int hour, int inRange, double outOfRange = 220) =>
        Hour(hour, 7, Enumerable.Range(0, 12).Select(i => i < inRange ? 120d : outOfRange).ToArray());

    /// <summary>
    /// A rankable hour of 84 readings, twelve a day on seven days, all at 120 except the first
    /// <paramref name="outOfRange"/> of the 84, which are at <paramref name="value"/>.
    /// </summary>
    private static IEnumerable<SensorGlucose> RankableHourWith(int hour, int outOfRange, double value = 220) =>
        Hour(hour, 7, Enumerable.Repeat(120d, 12).ToArray())
            .OrderBy(e => e.Timestamp)
            .Select((e, i) => { if (i < outOfRange) e.Mgdl = value; return e; })
            .ToList();

    private static void AssertColumnsApart(HourlyPatterns result)
    {
        foreach (var best in result.BestHours)
        foreach (var worst in result.WorstHours)
            (best.InRange - worst.InRange).Should().BeGreaterThanOrEqualTo(
                StatisticsService.HourlyPatternsMinimumSpread,
                "hour {0} is named best and hour {1} worst", best.Hour, worst.Hour);
    }

    #region Timezone

    [Fact]
    public void CalculateHourlyPatterns_ReportsTheTimezoneItBucketedOn()
    {
        var stockholm = TimeZoneHelper.GetTimeZoneInfoFromId("Europe/Stockholm");

        var result = _service.CalculateHourlyPatterns([], stockholm);

        result.TimeZone.Should().Be(stockholm.Id);
        result.ClockBasis.Should().Be(HourlyClockBasis.TenantTimeZone);
    }

    [Fact]
    public void CalculateHourlyPatterns_WithoutATimezone_SaysItBucketedOnEachReadingsOwnOffset()
    {
        var reading = new SensorGlucose
        {
            Mgdl = 100,
            Timestamp = new DateTime(2026, 1, 15, 14, 30, 0, DateTimeKind.Utc),
            UtcOffset = -300,
        };

        var result = _service.CalculateHourlyPatterns([reading], null);

        result.ClockBasis.Should().Be(HourlyClockBasis.ReadingOffsets);
        result.TimeZone.Should().BeNull();
        result.Hours.Single(h => h.Hour == 9).Count.Should().Be(1);
    }

    [Fact]
    public void CalculateHourlyPatterns_PublishesTheBandThresholdsItClassifiedOn()
    {
        var result = _service.CalculateHourlyPatterns([], TimeZoneInfo.Utc);

        result.Thresholds.Should().BeSameAs(_service.HourlyBandThresholds);
        result.Thresholds.Should().BeEquivalentTo(new GlycemicThresholds());
    }

    #endregion

    #region Per-hour figures

    [Fact]
    public void CalculateHourlyPatterns_ReportsEveryHourOfTheDayInOrder()
    {
        var result = _service.CalculateHourlyPatterns(Hour(5, 1, 100), TimeZoneInfo.Utc);

        result.Hours.Select(h => h.Hour).Should().Equal(Enumerable.Range(0, 24));
        result.Hours.Single(h => h.Hour == 5).Count.Should().Be(1);
    }

    [Fact]
    public void CalculateHourlyPatterns_SplitsEachHourAroundTheConsensusRange()
    {
        // 69 below; 70 and 180 are the inclusive edges of the range; 181 above.
        var result = _service.CalculateHourlyPatterns(Hour(2, 1, 69, 70, 180, 181), TimeZoneInfo.Utc);

        var hour = result.Hours.Single(h => h.Hour == 2);
        hour.BelowRange.Should().Be(25);
        hour.InRange.Should().Be(50);
        hour.AboveRange.Should().Be(25);
    }

    [Fact]
    public void CalculateHourlyPatterns_RoundsEachShareFromTheReadingsRatherThanFromRoundedBands()
    {
        // 1 very low + 2 low of 3 readings: summing the rounded bands would give 33.3 + 66.7.
        var result = _service.CalculateHourlyPatterns(Hour(2, 1, 50, 60, 65), TimeZoneInfo.Utc);

        result.Hours.Single(h => h.Hour == 2).BelowRange.Should().Be(100);
    }

    [Theory]
    [InlineData(new double[] { 60, 60, 200 }, HourlyExcursion.Below)]
    [InlineData(new double[] { 60, 200, 200 }, HourlyExcursion.Above)]
    [InlineData(new double[] { 60, 200 }, HourlyExcursion.Mixed)]
    [InlineData(new double[] { 100, 120 }, HourlyExcursion.None)]
    public void CalculateHourlyPatterns_NamesTheSideAnHourMostlyLeavesRangeOn(double[] values, HourlyExcursion expected)
    {
        var result = _service.CalculateHourlyPatterns(Hour(6, 1, values), TimeZoneInfo.Utc);

        result.Hours.Single(h => h.Hour == 6).MainExcursion.Should().Be(expected);
    }

    [Fact]
    public void CalculateHourlyPatterns_GivesAnHourWithNoReadingsNoExcursion()
    {
        var result = _service.CalculateHourlyPatterns([], TimeZoneInfo.Utc);

        result.Hours.Should().OnlyContain(h => h.MainExcursion == HourlyExcursion.None && !h.IsRanked);
    }

    #endregion

    #region Ranking thresholds

    [Fact]
    public void CalculateHourlyPatterns_PublishesItsRankingThresholds()
    {
        var result = _service.CalculateHourlyPatterns([], TimeZoneInfo.Utc);

        result.MinimumDaysToRank.Should().Be(StatisticsService.HourlyPatternsMinimumDays);
        result.MinimumReadingsToRank.Should().Be(StatisticsService.HourlyPatternsMinimumReadings);
        result.MinimumSpreadToRank.Should().Be(StatisticsService.HourlyPatternsMinimumSpread);
        result.MinimumLowDaysToList.Should().Be(StatisticsService.HourlyPatternsMinimumLowDays);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotRankAnHourSeenOnTooFewDays()
    {
        var days = StatisticsService.HourlyPatternsMinimumDays;
        var twelve = Enumerable.Repeat(120d, 12).ToArray();
        var entries = Hour(3, days - 1, twelve).Concat(Hour(4, days, twelve));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Hours.Single(h => h.Hour == 3).IsRanked.Should().BeFalse();
        result.Hours.Single(h => h.Hour == 4).IsRanked.Should().BeTrue();
        result.RankedHourCount.Should().Be(1);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotRankAnHourWithTooFewReadings()
    {
        var days = StatisticsService.HourlyPatternsMinimumDays;
        var perDay = (int)Math.Ceiling((double)StatisticsService.HourlyPatternsMinimumReadings / days);
        var sparse = Hour(3, days, 120);
        var dense = Hour(4, days, Enumerable.Repeat(120d, perDay).ToArray());

        var result = _service.CalculateHourlyPatterns(sparse.Concat(dense), TimeZoneInfo.Utc);

        result.Hours.Single(h => h.Hour == 3).IsRanked.Should().BeFalse();
        result.Hours.Single(h => h.Hour == 4).IsRanked.Should().BeTrue();
    }

    #endregion

    #region Comparison outcome

    [Fact]
    public void CalculateHourlyPatterns_ReportsNoReadingsWhenTheWindowIsEmpty()
    {
        var result = _service.CalculateHourlyPatterns([], TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.NoReadings);
    }

    [Fact]
    public void CalculateHourlyPatterns_ReportsTooLittleDataWithFewerThanTwoRankedHours()
    {
        var entries = RankableHour(9, 6).Concat(Hour(10, 1, 120));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.TooLittleData);
    }

    [Fact]
    public void CalculateHourlyPatterns_ReportsCloseTogetherWhenNoRankedHourStandsOut()
    {
        var entries = RankableHour(0, 12).Concat(RankableHour(1, 12));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.CloseTogether);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotRankHoursCloserThanTheMinimumSpread()
    {
        // 100% against 96.4%: a 3.6-point spread, under the 5-point floor.
        var entries = RankableHourWith(0, 0).Concat(RankableHourWith(1, 3)).Concat(RankableHourWith(2, 1));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.CloseTogether);
        result.BestHours.Should().BeEmpty();
        result.WorstHours.Should().BeEmpty();
    }

    [Fact]
    public void CalculateHourlyPatterns_RanksHoursAtLeastTheMinimumSpreadApart()
    {
        // 100% against 94.0%: a 6-point spread.
        var entries = RankableHourWith(0, 0).Concat(RankableHourWith(1, 5));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.Ranked);
        result.BestHours.Select(h => h.Hour).Should().Equal(0);
        result.WorstHours.Select(h => h.Hour).Should().Equal(1);
    }

    [Fact]
    public void CalculateHourlyPatterns_ReportsRankedWhenHoursDiffer()
    {
        var entries = RankableHour(0, 12).Concat(RankableHour(1, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.Comparison.Should().Be(HourlyComparison.Ranked);
    }

    #endregion

    #region Best and worst hours

    [Fact]
    public void CalculateHourlyPatterns_PicksTheThreeBestAndThreeWorstHoursByTimeInRange()
    {
        var entries = new[] { (0, 12), (1, 11), (2, 10), (3, 9), (4, 8), (5, 7), (6, 6), (7, 5) }
            .SelectMany(h => RankableHour(h.Item1, h.Item2));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0, 1, 2);
        result.WorstHours.Select(h => h.Hour).Should().Equal(7, 6, 5);
    }

    [Fact]
    public void CalculateHourlyPatterns_NeverNamesAnHourBothBestAndWorst()
    {
        var entries = new[] { (0, 12), (1, 10), (2, 8), (3, 6) }
            .SelectMany(h => RankableHour(h.Item1, h.Item2));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0, 1);
        result.WorstHours.Select(h => h.Hour).Should().Equal(3, 2);
    }

    [Fact]
    public void CalculateHourlyPatterns_LeavesUnrankedHoursOutOfBestAndWorst()
    {
        var entries = RankableHour(0, 12)
            .Concat(RankableHour(1, 6))
            .Concat(Hour(2, 1, 40, 40, 40));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0);
        result.WorstHours.Select(h => h.Hour).Should().Equal(1);
    }

    [Fact]
    public void CalculateHourlyPatterns_NamesNoBestOrWorstFromASingleRankedHour()
    {
        var result = _service.CalculateHourlyPatterns(RankableHour(9, 6), TimeZoneInfo.Utc);

        result.BestHours.Should().BeEmpty();
        result.WorstHours.Should().BeEmpty();
    }

    [Fact]
    public void CalculateHourlyPatterns_NamesNoBestOrWorstWhenEveryRankedHourIsInRangeAlike()
    {
        var entries = Enumerable.Range(0, 24).SelectMany(h => RankableHour(h, 12));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.RankedHourCount.Should().Be(24);
        result.BestHours.Should().BeEmpty();
        result.WorstHours.Should().BeEmpty();
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotCallAnHourWorstThatTiesTheBest()
    {
        // Five hours fully in range and one at half: only the half hour is worse than the rest.
        var entries = Enumerable.Range(0, 5).SelectMany(h => RankableHour(h, 12))
            .Concat(RankableHour(5, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.WorstHours.Select(h => h.Hour).Should().Equal(5);
        result.BestHours.Should().BeEmpty("the five full hours tie one another past the list's end");
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotCallAnHourBestThatTiesTheWorst()
    {
        var entries = RankableHour(0, 12)
            .Concat(Enumerable.Range(1, 5).SelectMany(h => RankableHour(h, 6)));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0);
        result.WorstHours.Should().BeEmpty("the five half hours tie one another past the list's end");
    }

    [Fact]
    public void CalculateHourlyPatterns_NamesNoHourWhoseTimeInRangeTiesAnHourInTheOtherColumn()
    {
        // 100%, then four hours tied at 75%, then 50%.
        var entries = RankableHour(0, 12)
            .Concat(Enumerable.Range(1, 4).SelectMany(h => RankableHour(h, 9)))
            .Concat(RankableHour(5, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0);
        result.WorstHours.Select(h => h.Hour).Should().Equal(5);
        AssertColumnsApart(result);
    }

    [Fact]
    public void CalculateHourlyPatterns_KeepsEveryNamedBestHourTheMinimumSpreadAboveEveryNamedWorstHour()
    {
        // 100%, 98.8%, 97.6% and 92.9%: 97.6 sits within the spread of both best hours.
        var entries = RankableHourWith(0, 0)
            .Concat(RankableHourWith(1, 1))
            .Concat(RankableHourWith(2, 2))
            .Concat(RankableHourWith(3, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Select(h => h.Hour).Should().Equal(0, 1);
        result.WorstHours.Select(h => h.Hour).Should().Equal(3);
        AssertColumnsApart(result);
    }

    [Fact]
    public void CalculateHourlyPatterns_TrimsTheMiddleUntilTheColumnsAreTheMinimumSpreadApart()
    {
        // Six hours at 100, 97.6, 96.4, 95.2, 94.0 and 90.5%.
        var entries = new[] { 0, 2, 3, 4, 5, 8 }
            .SelectMany((outOfRange, hour) => RankableHourWith(hour, outOfRange));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Should().NotBeEmpty();
        result.WorstHours.Should().NotBeEmpty();
        AssertColumnsApart(result);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotNameHoursThatTieTheFirstHourLeftOffTheList()
    {
        // 23 hours tied at 83.3%: which three are "most in range" would come down to the clock.
        var entries = Enumerable.Range(0, 23).SelectMany(h => RankableHour(h, 10))
            .Concat(RankableHour(23, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.BestHours.Should().BeEmpty();
        result.WorstHours.Select(h => h.Hour).Should().Equal(23);
        result.Comparison.Should().Be(HourlyComparison.Ranked);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotListBelowRangeHoursThatTieTheFirstHourLeftOff()
    {
        var entries = Enumerable.Range(0, 4).SelectMany(h => RankableHour(h, 10, outOfRange: 60));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.MostBelowRangeHours.Should().BeEmpty();
    }

    [Fact]
    public void CalculateHourlyPatterns_BreaksATimeInRangeTieOnTimeBelowRange()
    {
        // Equal time in range; hour 1 spends its time out of range low rather than high.
        var entries = RankableHour(0, 12)
            .Concat(RankableHour(1, 6, outOfRange: 60))
            .Concat(RankableHour(2, 6, outOfRange: 220))
            .Concat(RankableHour(3, 12));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.WorstHours.Select(h => h.Hour).Should().Equal(1, 2);
    }

    [Fact]
    public void CalculateHourlyPatterns_ListsTheRankedHoursWithTheMostTimeBelowRange()
    {
        var entries = RankableHour(0, 10, outOfRange: 60)
            .Concat(RankableHour(1, 8, outOfRange: 60))
            .Concat(RankableHour(2, 11, outOfRange: 60))
            .Concat(RankableHour(3, 4, outOfRange: 60))
            .Concat(RankableHour(4, 6, outOfRange: 220))
            .Concat(Hour(5, 1, 40, 40, 40));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.MostBelowRangeHours.Select(h => h.Hour).Should().Equal(3, 1, 0);
    }

    [Fact]
    public void CalculateHourlyPatterns_DoesNotListAnHourBelowRangeOnFewerThanTheMinimumLowDays()
    {
        // Hour 1 has one low reading; hour 2 has one low on each of its first two days.
        var entries = RankableHour(0, 12)
            .Concat(RankableHourWith(1, 1, value: 60))
            .Concat(Hour(2, 7, Enumerable.Repeat(120d, 12).ToArray())
                .Select(e => { if (e.Timestamp.Minute == 0 && e.Timestamp.Day <= FirstDay.Day + 1) e.Mgdl = 60; return e; }));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.MostBelowRangeHours.Select(h => h.Hour).Should().Equal(2);
    }

    [Fact]
    public void CalculateHourlyPatterns_ListsNoBelowRangeHoursWhenNoneWentLow()
    {
        var entries = RankableHour(0, 12).Concat(RankableHour(1, 6));

        var result = _service.CalculateHourlyPatterns(entries, TimeZoneInfo.Utc);

        result.MostBelowRangeHours.Should().BeEmpty();
    }

    #endregion
}
