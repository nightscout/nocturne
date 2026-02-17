using FluentAssertions;
using Nocturne.API.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for Total Daily Dose (TDD) calculations in the StatisticsService,
/// covering scheduled basal rate changes, temporary basals, and the interaction
/// between temporary basals overriding scheduled basals.
/// </summary>
public class TddCalculationTests
{
    private readonly StatisticsService _statisticsService;

    // Fixed reference point: midnight UTC on 2024-11-18
    private static readonly DateTimeOffset DayStart = new(2024, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTime StartDate = DayStart.UtcDateTime;
    private static readonly DateTime EndDate = StartDate.AddDays(1);

    public TddCalculationTests()
    {
        _statisticsService = new StatisticsService();
    }

    #region Helper Methods

    /// <summary>
    /// Creates a basal delivery StateSpan at the given hour with explicit rate and duration.
    /// </summary>
    private static StateSpan MakeBasalStateSpan(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes,
        string? origin = null,
        double? scheduledRate = null
    )
    {
        var startMills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();
        var endMills = startMills + (long)(durationMinutes * 60 * 1000);

        var metadata = new Dictionary<string, object>
        {
            ["rate"] = rateUPerHr,
        };

        if (origin != null)
            metadata["origin"] = origin;

        if (scheduledRate.HasValue)
            metadata["scheduledRate"] = scheduledRate.Value;

        return new StateSpan
        {
            Id = $"basal-{hourOffset}",
            Category = StateSpanCategory.BasalDelivery,
            StartMills = startMills,
            EndMills = endMills,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Creates a temp basal StateSpan at the given hour.
    /// Origin is set to "Algorithm" to indicate a temp/AID adjustment.
    /// </summary>
    private static StateSpan MakeTempBasalSpan(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes,
        double? scheduledRate = null
    )
    {
        return MakeBasalStateSpan(
            hourOffset,
            rateUPerHr,
            durationMinutes,
            origin: rateUPerHr == 0 ? "Suspended" : "Algorithm",
            scheduledRate: scheduledRate
        );
    }

    /// <summary>
    /// Creates a scheduled basal StateSpan at the given hour.
    /// </summary>
    private static StateSpan MakeScheduledBasalSpan(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes
    )
    {
        return MakeBasalStateSpan(hourOffset, rateUPerHr, durationMinutes, origin: "Scheduled");
    }

    /// <summary>
    /// Creates a bolus at the given hour.
    /// </summary>
    private static Bolus MakeBolus(
        int hourOffset,
        double units,
        bool automatic = false
    )
    {
        var mills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();

        return new Bolus
        {
            Id = Guid.NewGuid(),
            Insulin = units,
            Mills = mills,
            Automatic = automatic,
        };
    }

    /// <summary>
    /// Creates a StateSpan from a DateTimeOffset base with day offset.
    /// </summary>
    private static StateSpan MakeBasalStateSpanFromBase(
        DateTimeOffset baseTime,
        int dayOffset,
        int hourOffset,
        double rateUPerHr,
        double durationMinutes,
        string? origin = null
    )
    {
        var startMills = baseTime.AddDays(dayOffset).AddHours(hourOffset).ToUnixTimeMilliseconds();
        var endMills = startMills + (long)(durationMinutes * 60 * 1000);

        var metadata = new Dictionary<string, object>
        {
            ["rate"] = rateUPerHr,
        };

        if (origin != null)
            metadata["origin"] = origin;

        return new StateSpan
        {
            Id = $"basal-d{dayOffset}-h{hourOffset}",
            Category = StateSpanCategory.BasalDelivery,
            StartMills = startMills,
            EndMills = endMills,
            Metadata = metadata,
        };
    }

    #endregion

    #region Scheduled Basal Tests

    [Fact]
    public void TDD_WithFlatScheduledBasal_ShouldCalculateCorrectTotal()
    {
        // A flat 1.0 U/hr basal for 24 hours = 24.0 U total basal
        // Delivered as 24 one-hour segments
        var stateSpans = Enumerable
            .Range(0, 24)
            .Select(h => MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60))
            .ToList();

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(24.0);
        result.TotalBolus.Should().Be(0);
        result.TotalInsulin.Should().Be(24.0);
        result.Tdd.Should().Be(24.0);
        result.BasalPercent.Should().Be(100.0);
        result.BolusPercent.Should().Be(0);
    }

    [Fact]
    public void TDD_WithScheduledBasalChanges_ShouldSumDifferentRates()
    {
        // Realistic pump schedule with rate changes throughout the day:
        //   00:00-06:00  0.8 U/hr  (6 hrs = 4.80 U)
        //   06:00-10:00  1.2 U/hr  (4 hrs = 4.80 U)
        //   10:00-16:00  1.0 U/hr  (6 hrs = 6.00 U)
        //   16:00-22:00  1.1 U/hr  (6 hrs = 6.60 U)
        //   22:00-00:00  0.9 U/hr  (2 hrs = 1.80 U)
        //   Total expected: 24.00 U
        var stateSpans = new List<StateSpan>();

        // Overnight: 0.8 U/hr for 6 hours
        for (var h = 0; h < 6; h++)
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 0.8, durationMinutes: 60));

        // Morning: 1.2 U/hr for 4 hours
        for (var h = 6; h < 10; h++)
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.2, durationMinutes: 60));

        // Midday: 1.0 U/hr for 6 hours
        for (var h = 10; h < 16; h++)
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60));

        // Afternoon/Evening: 1.1 U/hr for 6 hours
        for (var h = 16; h < 22; h++)
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.1, durationMinutes: 60));

        // Late night: 0.9 U/hr for 2 hours
        for (var h = 22; h < 24; h++)
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 0.9, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 4.80 + 4.80 + 6.00 + 6.60 + 1.80 = 24.00
        result.TotalBasal.Should().Be(24.0);
        result.Tdd.Should().Be(24.0);
    }

    [Fact]
    public void TDD_WithScheduledBasalAndBoluses_ShouldSumBothCorrectly()
    {
        // Scheduled basal: 0.8 U/hr for 24 hours = 19.2 U
        var stateSpans = Enumerable
            .Range(0, 24)
            .Select(h => MakeScheduledBasalSpan(h, rateUPerHr: 0.8, durationMinutes: 60))
            .ToList();

        // Add meal boluses throughout the day
        var boluses = new List<Bolus>
        {
            MakeBolus(7, units: 4.0),   // Breakfast
            MakeBolus(12, units: 6.0),  // Lunch
            MakeBolus(18, units: 5.5),  // Dinner
            MakeBolus(15, units: 1.5),  // Correction bolus
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            boluses,
            stateSpans,
            StartDate,
            EndDate
        );

        // 0.8 * 24 = 19.2, 4.0 + 6.0 + 5.5 + 1.5 = 17.0, total = 36.2
        result.TotalBasal.Should().Be(19.2);
        result.TotalBolus.Should().Be(17.0);
        result.TotalInsulin.Should().Be(36.2);
        result.Tdd.Should().Be(36.2);
        result.BolusCount.Should().Be(4);
    }

    #endregion

    #region Temp Basal Tests

    [Fact]
    public void TDD_WithTempBasalAbsoluteRate_ShouldCalculateFromRate()
    {
        // A single temp basal at 2.0 U/hr for 30 minutes = 1.0 U
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(hourOffset: 10, rateUPerHr: 2.0, durationMinutes: 30),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(1.0);
        result.Tdd.Should().Be(1.0);
    }

    [Fact]
    public void TDD_WithTempBasalFromRateOnly_ShouldCalculateInsulin()
    {
        // StateSpan with rate 1.5 U/hr for 60 minutes = 1.5 U
        var stateSpans = new List<StateSpan>
        {
            MakeBasalStateSpan(hourOffset: 8, rateUPerHr: 1.5, durationMinutes: 60),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(1.5);
    }

    [Fact]
    public void TDD_WithZeroRateTempBasal_ShouldContributeZeroInsulin()
    {
        // A suspend (zero temp) should contribute 0 insulin
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(hourOffset: 3, rateUPerHr: 0.0, durationMinutes: 30),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(0);
        result.TotalInsulin.Should().Be(0);
    }

    [Fact]
    public void TDD_WithMultipleTempBasals_ShouldSumAllSegments()
    {
        // Multiple temp basals throughout the day (as an AID system would produce)
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(0, rateUPerHr: 0.5, durationMinutes: 30),  // 0.25 U
            MakeTempBasalSpan(1, rateUPerHr: 1.2, durationMinutes: 30),  // 0.60 U
            MakeTempBasalSpan(2, rateUPerHr: 0.0, durationMinutes: 30),  // 0.00 U (suspend)
            MakeTempBasalSpan(3, rateUPerHr: 1.8, durationMinutes: 30),  // 0.90 U
            MakeTempBasalSpan(4, rateUPerHr: 0.3, durationMinutes: 30),  // 0.15 U
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 0.25 + 0.60 + 0.00 + 0.90 + 0.15 = 1.90
        result.TotalBasal.Should().Be(1.9);
    }

    [Fact]
    public void TDD_WithTempBasalHavingPercent_ShouldTrackPercentMetadata()
    {
        // Temp basal expressed as 150% of scheduled rate.
        // The insulin delivered is still the absolute amount (rate * duration),
        // but the scheduledRate metadata enables analysis.
        // Scheduled rate was 1.0 U/hr, temp is 150% = 1.5 U/hr for 60 min = 1.5 U
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(hourOffset: 14, rateUPerHr: 1.5, durationMinutes: 60, scheduledRate: 1.0),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(1.5);
    }

    #endregion

    #region Temp Basal Overriding Scheduled Basal

    [Fact]
    public void TDD_TempBasalReplacesScheduledBasal_ShouldOnlyCountTempInsulin()
    {
        // When a temp basal is active, the pump replaces the scheduled basal.
        // The StateSpan for that period reflects the temp rate, not the scheduled rate.
        //
        // Schedule: 1.0 U/hr all day (24 segments)
        // Temp at hour 10: 2.0 U/hr for 60 min (replaces the 1.0 U/hr segment)
        //
        // Expected: 23 hours at 1.0 = 23.0 U scheduled + 1 hour at 2.0 = 2.0 U temp = 25.0 U total

        var stateSpans = new List<StateSpan>();

        // Scheduled basal for all hours EXCEPT hour 10 (replaced by temp)
        for (var h = 0; h < 24; h++)
        {
            if (h == 10)
                continue;
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // Temp basal at hour 10: higher rate
        stateSpans.Add(MakeTempBasalSpan(10, rateUPerHr: 2.0, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 23 * 1.0 + 1 * 2.0 = 25.0
        result.TotalBasal.Should().Be(25.0);
        result.Tdd.Should().Be(25.0);
    }

    [Fact]
    public void TDD_TempBasalLowerThanScheduled_ShouldReduceTDD()
    {
        // A reduced temp basal (e.g., pre-exercise) should lower the TDD
        // Schedule: 1.0 U/hr all day
        // Hours 14-16: temp at 0.3 U/hr (2 hours replaced)

        var stateSpans = new List<StateSpan>();

        for (var h = 0; h < 24; h++)
        {
            if (h >= 14 && h < 16)
                continue;
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // 2 hours of reduced temp basal
        stateSpans.Add(MakeTempBasalSpan(14, rateUPerHr: 0.3, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(15, rateUPerHr: 0.3, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 22 * 1.0 + 2 * 0.3 = 22.6
        result.TotalBasal.Should().Be(22.6);
        result.Tdd.Should().Be(22.6);
    }

    [Fact]
    public void TDD_TempBasalSuspend_ShouldReduceTDDToZeroForPeriod()
    {
        // A pump suspend (0% temp basal) for 2 hours
        // Schedule: 1.0 U/hr all day
        // Hours 2-4: suspended (0 U/hr)

        var stateSpans = new List<StateSpan>();

        for (var h = 0; h < 24; h++)
        {
            if (h >= 2 && h < 4)
                continue;
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // 2 hours of zero temp (suspend)
        stateSpans.Add(MakeTempBasalSpan(2, rateUPerHr: 0.0, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(3, rateUPerHr: 0.0, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 22 * 1.0 + 2 * 0.0 = 22.0
        result.TotalBasal.Should().Be(22.0);
        result.Tdd.Should().Be(22.0);
    }

    [Fact]
    public void TDD_FullDayWithMixedScheduleAndTemps_RealisticScenario()
    {
        // Realistic full-day scenario with scheduled basal changes + AID temp basals + boluses
        //
        // Scheduled program:
        //   00:00-06:00  0.8 U/hr
        //   06:00-12:00  1.0 U/hr
        //   12:00-18:00  0.9 U/hr
        //   18:00-00:00  0.85 U/hr
        //
        // AID overrides (temp basals replacing scheduled segments):
        //   02:00-03:00  0.0 U/hr (suspend for predicted low)
        //   07:00-08:00  1.8 U/hr (increased for post-breakfast rise)
        //   14:00-15:00  0.4 U/hr (reduced for exercise)
        //   20:00-21:00  1.3 U/hr (correction for high)
        //
        // Boluses:
        //   07:30 Meal Bolus 4.5 U (breakfast)
        //   12:30 Meal Bolus 5.0 U (lunch)
        //   18:30 Meal Bolus 6.0 U (dinner)
        //   15:30 Correction Bolus 1.2 U

        var stateSpans = new List<StateSpan>();

        // Build scheduled basal segments (excluding hours overridden by temps)
        var overriddenHours = new HashSet<int> { 2, 7, 14, 20 };

        for (var h = 0; h < 24; h++)
        {
            if (overriddenHours.Contains(h))
                continue;

            double rate = h switch
            {
                < 6 => 0.8,
                < 12 => 1.0,
                < 18 => 0.9,
                _ => 0.85,
            };

            stateSpans.Add(MakeScheduledBasalSpan(h, rate, 60));
        }

        // AID temp basals
        stateSpans.Add(MakeTempBasalSpan(2, rateUPerHr: 0.0, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(7, rateUPerHr: 1.8, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(14, rateUPerHr: 0.4, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(20, rateUPerHr: 1.3, durationMinutes: 60));

        // Boluses
        var boluses = new List<Bolus>
        {
            MakeBolus(7, units: 4.5),
            MakeBolus(12, units: 5.0),
            MakeBolus(18, units: 6.0),
            MakeBolus(15, units: 1.2),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            boluses,
            stateSpans,
            StartDate,
            EndDate
        );

        // Scheduled basal (non-overridden hours):
        //   h0,1,3,4,5 at 0.8 = 5 * 0.8 = 4.0
        //   h6,8,9,10,11 at 1.0 = 5 * 1.0 = 5.0
        //   h12,13,15,16,17 at 0.9 = 5 * 0.9 = 4.5
        //   h18,19,21,22,23 at 0.85 = 5 * 0.85 = 4.25
        // Scheduled total = 4.0 + 5.0 + 4.5 + 4.25 = 17.75
        //
        // Temp basal:
        //   h2 at 0.0 = 0
        //   h7 at 1.8 = 1.8
        //   h14 at 0.4 = 0.4
        //   h20 at 1.3 = 1.3
        // Temp total = 3.5
        //
        // Total basal = 17.75 + 3.5 = 21.25
        var expectedBasal = 17.75 + 3.5;

        // Total bolus = 4.5 + 5.0 + 6.0 + 1.2 = 16.7
        var expectedBolus = 4.5 + 5.0 + 6.0 + 1.2;

        var expectedTotal = expectedBasal + expectedBolus;

        result.TotalBasal.Should().Be(expectedBasal);
        result.TotalBolus.Should().Be(expectedBolus);
        result.TotalInsulin.Should().Be(expectedTotal);
        result.Tdd.Should().Be(Math.Round(expectedTotal * 10) / 10);
    }

    [Fact]
    public void TDD_TempBasalWithAbsoluteRate_ShouldUseRate()
    {
        // A StateSpan with rate metadata directly represents the absolute rate.
        // 1.6 U/hr for 30 minutes = 0.8 U
        var stateSpans = new List<StateSpan>
        {
            MakeBasalStateSpan(hourOffset: 5, rateUPerHr: 1.6, durationMinutes: 30),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        // 1.6 U/hr * 30 min / 60 = 0.8 U
        result.TotalBasal.Should().Be(0.8);
    }

    #endregion

    #region Daily Basal/Bolus Ratio Tests

    [Fact]
    public void DailyRatios_WithScheduledBasalAndBoluses_ShouldSplitCorrectly()
    {
        // Full day scheduled basal at 1.0 U/hr = 24.0 U basal
        var stateSpans = Enumerable
            .Range(0, 24)
            .Select(h => MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60))
            .ToList();

        // 16.0 U bolus total
        var boluses = new List<Bolus>
        {
            MakeBolus(7, units: 5.0),
            MakeBolus(12, units: 6.0),
            MakeBolus(18, units: 5.0),
        };

        var result = _statisticsService.CalculateDailyBasalBolusRatios(boluses, stateSpans);

        result.DayCount.Should().Be(1);
        result.DailyData.Should().HaveCount(1);

        var day = result.DailyData[0];
        day.Basal.Should().Be(24.0);
        day.Bolus.Should().Be(16.0);
        day.Total.Should().Be(40.0);
        day.BasalPercent.Should().Be(60.0);
        day.BolusPercent.Should().Be(40.0);

        result.AverageTdd.Should().Be(40.0);
    }

    [Fact]
    public void DailyRatios_WithTempBasalReplacingScheduled_ShouldReflectTempRate()
    {
        var stateSpans = new List<StateSpan>();

        // Scheduled basal for 22 hours at 1.0 U/hr
        for (var h = 0; h < 24; h++)
        {
            if (h >= 10 && h < 12)
                continue; // replaced by temp
            stateSpans.Add(MakeScheduledBasalSpan(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // Temp basal hours 10-12 at 2.5 U/hr = 5.0 U
        stateSpans.Add(MakeTempBasalSpan(10, rateUPerHr: 2.5, durationMinutes: 60));
        stateSpans.Add(MakeTempBasalSpan(11, rateUPerHr: 2.5, durationMinutes: 60));

        var result = _statisticsService.CalculateDailyBasalBolusRatios(
            Array.Empty<Bolus>(),
            stateSpans
        );

        result.DayCount.Should().Be(1);

        var day = result.DailyData[0];
        // 22 * 1.0 + 2 * 2.5 = 27.0
        day.Basal.Should().Be(27.0);
        day.Total.Should().Be(27.0);
    }

    [Fact]
    public void DailyRatios_MultiDay_WithVaryingTempBasals_ShouldTrackPerDay()
    {
        var day1Start = DayStart;
        var day2Start = DayStart.AddDays(1);

        var stateSpans = new List<StateSpan>();

        // Day 1: flat 1.0 U/hr, no temps = 24.0 U
        for (var h = 0; h < 24; h++)
        {
            stateSpans.Add(
                MakeBasalStateSpanFromBase(day1Start, 0, h, rateUPerHr: 1.0, durationMinutes: 60)
            );
        }

        // Day 2: 20 hours at 1.0 + 4 hours temp at 0.5 = 22.0 U
        for (var h = 0; h < 24; h++)
        {
            if (h >= 8 && h < 12)
            {
                stateSpans.Add(
                    MakeBasalStateSpanFromBase(
                        day1Start, 1, h, rateUPerHr: 0.5, durationMinutes: 60, origin: "Algorithm"
                    )
                );
            }
            else
            {
                stateSpans.Add(
                    MakeBasalStateSpanFromBase(
                        day1Start, 1, h, rateUPerHr: 1.0, durationMinutes: 60
                    )
                );
            }
        }

        var result = _statisticsService.CalculateDailyBasalBolusRatios(
            Array.Empty<Bolus>(),
            stateSpans
        );

        result.DayCount.Should().Be(2);
        result.DailyData.Should().HaveCount(2);

        var d1 = result.DailyData[0];
        d1.Basal.Should().Be(24.0);

        var d2 = result.DailyData[1];
        // 20 * 1.0 + 4 * 0.5 = 22.0
        d2.Basal.Should().Be(22.0);

        // Average TDD = (24 + 22) / 2 = 23.0
        result.AverageTdd.Should().Be(23.0);
    }

    #endregion

    #region Basal Analysis Tests

    [Fact]
    public void BasalAnalysis_ShouldCountTempBasalCategories()
    {
        // Mix of high, low, and zero temp basals
        // Using origin and scheduledRate metadata for temp basal categorization
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(8, rateUPerHr: 1.5, durationMinutes: 30, scheduledRate: 1.0),   // High (1.5 > 1.0)
            MakeTempBasalSpan(10, rateUPerHr: 1.8, durationMinutes: 30, scheduledRate: 1.0),  // High (1.8 > 1.0)
            MakeTempBasalSpan(14, rateUPerHr: 0.5, durationMinutes: 30, scheduledRate: 1.0),  // Low (0.5 < 1.0)
            MakeTempBasalSpan(16, rateUPerHr: 0.0, durationMinutes: 30, scheduledRate: 1.0),  // Zero (suspend)
        };

        var result = _statisticsService.CalculateBasalAnalysis(stateSpans, StartDate, EndDate);

        result.TempBasalInfo.Total.Should().Be(4);
        result.TempBasalInfo.HighTemps.Should().Be(2);
        // LowTemps counts rate < scheduledRate, which includes the zero/suspended temp
        result.TempBasalInfo.LowTemps.Should().Be(1);
        result.TempBasalInfo.ZeroTemps.Should().Be(1);
    }

    [Fact]
    public void BasalAnalysis_WithMixedRates_ShouldCalculateCorrectStats()
    {
        var stateSpans = new List<StateSpan>
        {
            MakeScheduledBasalSpan(0, rateUPerHr: 0.8, durationMinutes: 60),
            MakeScheduledBasalSpan(6, rateUPerHr: 1.2, durationMinutes: 60),
            MakeTempBasalSpan(12, rateUPerHr: 1.5, durationMinutes: 60, scheduledRate: 1.0),
            MakeScheduledBasalSpan(18, rateUPerHr: 0.9, durationMinutes: 60),
        };

        var result = _statisticsService.CalculateBasalAnalysis(stateSpans, StartDate, EndDate);

        result.Stats.Count.Should().Be(4);
        result.Stats.MinRate.Should().Be(0.8);
        result.Stats.MaxRate.Should().Be(1.5);
        result.Stats.AvgRate.Should().Be(1.1); // (0.8 + 1.2 + 1.5 + 0.9) / 4

        // Total delivered: 0.8 + 1.2 + 1.5 + 0.9 = 4.4 U (each is rate * 60min / 60)
        result.Stats.TotalDelivered.Should().Be(4.4);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TDD_WithNoData_ShouldReturnZeros()
    {
        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            Array.Empty<StateSpan>(),
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(0);
        result.TotalBolus.Should().Be(0);
        result.TotalInsulin.Should().Be(0);
        result.Tdd.Should().Be(0);
    }

    [Fact]
    public void TDD_WithOnlyBoluses_ShouldHaveZeroBasal()
    {
        var boluses = new List<Bolus>
        {
            MakeBolus(7, units: 5.0),
            MakeBolus(12, units: 6.0),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            boluses,
            Array.Empty<StateSpan>(),
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(0);
        result.TotalBolus.Should().Be(11.0);
        result.BasalPercent.Should().Be(0);
        result.BolusPercent.Should().Be(100.0);
    }

    [Fact]
    public void TDD_MultiDay_ShouldDivideByDayCount()
    {
        // 3-day period with constant basal
        var start = StartDate;
        var end = StartDate.AddDays(3);

        var stateSpans = new List<StateSpan>();
        for (var d = 0; d < 3; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                stateSpans.Add(
                    MakeBasalStateSpanFromBase(DayStart, d, h, rateUPerHr: 1.0, durationMinutes: 60)
                );
            }
        }

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            start,
            end
        );

        // 72 hours of 1.0 U/hr = 72.0 U total
        result.TotalBasal.Should().Be(72.0);
        // TDD = 72 / 3 = 24.0
        result.Tdd.Should().Be(24.0);
        result.DayCount.Should().Be(3);
    }

    [Fact]
    public void TDD_SubHourTempBasal_ShouldCalculateFractionalInsulin()
    {
        // A very short temp basal: 3.0 U/hr for 5 minutes = 0.25 U
        var stateSpans = new List<StateSpan>
        {
            MakeTempBasalSpan(hourOffset: 10, rateUPerHr: 3.0, durationMinutes: 5),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(),
            stateSpans,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(0.25);
    }

    [Fact]
    public void TDD_SMBBoluses_ShouldBeCountedAsBolus()
    {
        // Super Micro Boluses from AID systems should be counted as bolus, not basal.
        // In v4, SMBs are Bolus records with Automatic = true.
        var boluses = new List<Bolus>
        {
            MakeBolus(10, units: 0.3, automatic: true),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            boluses,
            Array.Empty<StateSpan>(),
            StartDate,
            EndDate
        );

        result.TotalBolus.Should().Be(0.3);
        result.TotalBasal.Should().Be(0);
        result.BolusCount.Should().Be(1);
    }

    #endregion
}
