using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using static Nocturne.Core.Clinical.Tests.Statistics.WindowEnd;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class StatisticsServiceWindowClipTests
{
    private readonly StatisticsService _sut = new(TimeProvider.System);

    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddDays(1);

    private static List<TempBasal> RunningPastTheEnd() =>
    [
        TempBasalAt(1.0, Start.AddHours(20), Start.AddHours(21)),
        TempBasalAt(3.0, End.AddMinutes(-30), End.AddMinutes(90)),
    ];

    private static List<TempBasal> RunningFromBeforeTheStart() =>
    [
        TempBasalAt(2.0, Start.AddMinutes(-30), Start.AddMinutes(90)),
        TempBasalAt(1.0, Start.AddHours(3), Start.AddHours(4)),
    ];

    [Fact]
    public void BasalAnalysis_TempRunningPastTheWindowEnd_CountsOnlyTheTimeInsideIt()
    {
        var analysis = _sut.CalculateBasalAnalysis(RunningPastTheEnd(), Start, End, Mills(End), TimeZoneInfo.Utc);

        analysis.Stats.TotalDelivered.Should().Be(2.5);
        analysis.Stats.AvgRate.Should().Be(1.67);
        analysis.HourlyPercentiles[23].Count.Should().Be(1);
        analysis.HourlyPercentiles[0].Count.Should().Be(0);
    }

    [Fact]
    public void BasalAnalysis_TempRunningFromBeforeTheWindowStart_CountsOnlyTheTimeInsideIt()
    {
        var analysis = _sut.CalculateBasalAnalysis(RunningFromBeforeTheStart(), Start, End, Mills(End), TimeZoneInfo.Utc);

        analysis.Stats.TotalDelivered.Should().Be(4.0);
        analysis.Stats.AvgRate.Should().Be(1.6);
        analysis.HourlyPercentiles[23].Count.Should().Be(0);
        analysis.HourlyPercentiles[0].Count.Should().Be(1);
        analysis.HourlyPercentiles[1].Count.Should().Be(1);
    }

    [Fact]
    public void InsulinDelivery_TempStraddlingEitherEdge_CountsOnlyTheTimeInsideTheWindow()
    {
        var pastEnd = _sut.CalculateInsulinDeliveryStatistics(
            [], [], RunningPastTheEnd(), [], Start, End, Mills(End));
        var fromBefore = _sut.CalculateInsulinDeliveryStatistics(
            [], [], RunningFromBeforeTheStart(), [], Start, End, Mills(End));

        pastEnd.TotalBasal.Should().Be(2.5);
        fromBefore.TotalBasal.Should().Be(4.0);
    }

    [Fact]
    public void DailyRatios_TempStraddlingEitherEdge_CountsOnlyTheTimeInsideTheWindow()
    {
        var pastEnd = _sut.CalculateDailyBasalBolusRatios(
            [], [], RunningPastTheEnd(), Start, Mills(End), TimeZoneInfo.Utc);
        var fromBefore = _sut.CalculateDailyBasalBolusRatios(
            [], [], RunningFromBeforeTheStart(), Start, Mills(End), TimeZoneInfo.Utc);

        pastEnd.DailyData.Select(d => (d.Date, d.Basal)).Should().Equal(("2024-01-01", 2.5));
        fromBefore.DailyData.Select(d => (d.Date, d.Basal)).Should().Equal(("2024-01-01", 4.0));
    }

    [Fact]
    public void HourlyDelivery_TempStraddlingEitherEdge_CountsOnlyTheTimeInsideTheWindow()
    {
        var pastEnd = _sut.CalculateHourlyInsulinDelivery(
            RunningPastTheEnd(), [], [], Start, End, Mills(End), TimeZoneInfo.Utc);
        var fromBefore = _sut.CalculateHourlyInsulinDelivery(
            RunningFromBeforeTheStart(), [], [], Start, End, Mills(End), TimeZoneInfo.Utc);

        pastEnd.Hours[23].Basal.Should().Be(1.5);
        pastEnd.Hours[0].Basal.Should().Be(0);
        pastEnd.DaysWithData.Should().Be(1);
        fromBefore.Hours[23].Basal.Should().Be(0);
        fromBefore.Hours[0].Basal.Should().Be(2.0);
        fromBefore.Hours[1].Basal.Should().Be(1.0);
        fromBefore.DaysWithData.Should().Be(1);
    }

    [Fact]
    public void DeliveredWithin_WindowEndsBeforeAClosedTempDoes_StopsAtTheEnd()
    {
        var pieces = StatisticsService.DeliveredWithin(
            [TempBasalAt(2.0, Start.AddHours(1), Start.AddHours(5))], Start, Mills(Start.AddHours(2)));

        var piece = pieces.Should().ContainSingle().Subject;
        piece.StartMills.Should().Be(Mills(Start.AddHours(1)));
        piece.EndMills.Should().Be(Mills(Start.AddHours(2)));
    }

    [Fact]
    public void DeliveredWithin_TempEntirelyOutsideTheWindow_IsDropped()
    {
        StatisticsService.DeliveredWithin(
            [TempBasalAt(2.0, Start.AddHours(-3), Start.AddHours(-1))], Start, Mills(End))
            .Should().BeEmpty();
    }

    [Fact]
    public void TempBasalReadStart_ReachesBackTheLongestATempRuns_AndNeverUnderflows()
    {
        StatisticsService.TempBasalReadStart(Start).Should().Be(Start.AddHours(-24));
        StatisticsService.TempBasalReadStart(DateTime.MinValue).Should().Be(DateTime.MinValue);
    }

    private static TempBasal TempBasalAt(double rate, DateTime startUtc, DateTime endUtc) => new()
    {
        StartTimestamp = startUtc,
        EndTimestamp = endUtc,
        Rate = rate,
        Origin = TempBasalOrigin.Algorithm,
    };
}
