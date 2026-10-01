using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Pins the three rules nightscout/nocturne#1100 found written twice in
/// <see cref="StatisticsService"/>: hour-of-day slicing, the day count, and the glucose ceiling.
/// Each test feeds one dataset to both call sites and asserts they agree.
/// </summary>
public class StatisticsServiceOneRuleTests
{
    private readonly StatisticsService _sut = new();

    private static readonly TimeZoneInfo Kathmandu = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu");

    [Fact]
    public void HourOfDay_BasalAnalysisAndHourlyDelivery_SliceOnTheSameLocalHourBoundary()
    {
        // Kathmandu is UTC+5:45: 00:05-00:25 UTC is 05:50-06:10 local, which crosses the local
        // 06:00 boundary but no UTC hour boundary.
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var tempBasals = new[]
        {
            TempBasalAt(3.0, start.AddMinutes(5), start.AddMinutes(25)),
        };

        var analysis = _sut.CalculateBasalAnalysis(tempBasals, [], start, end, Kathmandu);
        var delivery = _sut.CalculateHourlyInsulinDelivery(tempBasals, [], [], start, end, Kathmandu);

        analysis.HourlyPercentiles[5].Count.Should().Be(1);
        analysis.HourlyPercentiles[6].Count.Should().Be(1);
        delivery.Hours[5].Basal.Should().Be(0.5);
        delivery.Hours[6].Basal.Should().Be(0.5);
        for (var hour = 0; hour < 24; hour++)
        {
            (analysis.HourlyPercentiles[hour].Count > 0)
                .Should().Be(delivery.Hours[hour].Basal > 0, $"hour {hour} is sliced by one rule");
        }
    }

    [Theory]
    [InlineData(7.4, 7)]
    [InlineData(7.6, 8)]
    [InlineData(0.2, 1)]
    public void WindowDays_IsOneRoundingOnEveryResponse(double windowDays, int expected)
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(windowDays);
        var tempBasals = new[] { TempBasalAt(1.0, start.AddHours(1), start.AddHours(2)) };
        var boluses = new[] { new Bolus { Insulin = 2.0, Timestamp = start.AddHours(3) } };

        var delivery = _sut.CalculateInsulinDeliveryStatistics(boluses, [], tempBasals, [], start, end);
        var analysis = _sut.CalculateBasalAnalysis(tempBasals, [], start, end);

        delivery.WindowDays.Should().Be(expected);
        analysis.WindowDays.Should().Be(expected);
        delivery.Tdd.Should().Be(Math.Round(3.0 / expected * 10) / 10);
    }

    [Fact]
    public void WindowDays_DoesNotGainADayFromTheEndBoundOrMillisecondJitter()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        StatisticsService.WindowDays(start, start.AddDays(7)).Should().Be(7);
        StatisticsService.WindowDays(start, start.AddDays(7).AddMilliseconds(-1)).Should().Be(7);
        StatisticsService.WindowDays(start.AddMilliseconds(-5), start.AddDays(7)).Should().Be(7);
    }

    [Fact]
    public void DaysWithData_CountsDaysHoldingInsulinOnBothResponses_NotTheWindow()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddDays(7);
        var tempBasals = new[] { TempBasalAt(1.0, start.AddDays(1).AddHours(8), start.AddDays(1).AddHours(9)) };
        var boluses = new[] { new Bolus { Insulin = 2.0, Timestamp = start.AddDays(4).AddHours(12) } };

        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, [], tempBasals);
        var hourly = _sut.CalculateHourlyInsulinDelivery(tempBasals, boluses, [], start, end);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(boluses, [], tempBasals, [], start, end);

        ratios.DaysWithData.Should().Be(2);
        hourly.DaysWithData.Should().Be(2);
        delivery.WindowDays.Should().Be(7);
    }

    [Theory]
    [InlineData(599.9, true)]
    [InlineData(GlucoseConstants.MaxPlausibleMgdl, false)]
    [InlineData(999, false)]
    public void GlucoseCeiling_IsTheSharedConstant(double mgdl, bool admitted)
    {
        GlucoseStatistics.IsPlausibleReading(mgdl).Should().Be(admitted);
    }

    [Fact]
    public void GlucoseCeiling_DistributionAndBasicStats_ShareOneDenominator()
    {
        var mgdl = new[] { 100.0, 250.0, 599.0, 600.0, 800.0, 999.0 };
        var entries = mgdl.Select((value, i) => new SensorGlucose
        {
            Mgdl = value,
            Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(5 * i),
        });

        var basic = _sut.CalculateBasicStats(mgdl);
        var distribution = _sut.CalculateGlucoseDistribution(entries).ToList();

        basic.Count.Should().Be(3);
        distribution.Sum(bin => bin.Count).Should().Be(basic.Count);
    }

    private static TempBasal TempBasalAt(double rate, DateTime startUtc, DateTime endUtc) => new()
    {
        StartTimestamp = startUtc,
        EndTimestamp = endUtc,
        Rate = rate,
        Origin = TempBasalOrigin.Algorithm,
    };
}
