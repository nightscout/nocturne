using FluentAssertions;
using Nocturne.API.Services;
using Nocturne.Core.Models;

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
    /// Creates a temp basal treatment at the given hour with explicit Rate and Duration.
    /// Sets _insulin via the Insulin property so the auto-calculation path is not used.
    /// </summary>
    private static Treatment MakeTempBasal(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes,
        double? percent = null
    )
    {
        var mills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();
        // Pre-calculate the insulin delivered so we set it explicitly,
        // matching what a pump uploader would do.
        var insulin = rateUPerHr * (durationMinutes / 60.0);

        return new Treatment
        {
            Id = $"temp-basal-{hourOffset}",
            EventType = "Temp Basal",
            Insulin = insulin,
            Rate = rateUPerHr,
            Duration = durationMinutes,
            Percent = percent,
            Mills = mills,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
        };
    }

    /// <summary>
    /// Creates a temp basal using only Rate and Duration (no explicit Insulin),
    /// which exercises the Treatment.Insulin auto-calculation from Rate * Duration / 60.
    /// </summary>
    private static Treatment MakeTempBasalFromRate(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes,
        double? percent = null
    )
    {
        var mills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();

        return new Treatment
        {
            Id = $"temp-basal-rate-{hourOffset}",
            EventType = "Temp Basal",
            Rate = rateUPerHr,
            Duration = durationMinutes,
            Percent = percent,
            Mills = mills,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
        };
    }

    /// <summary>
    /// Creates a scheduled basal segment (non-temp) at the given hour.
    /// These represent the pump's programmed basal delivery.
    /// </summary>
    private static Treatment MakeScheduledBasal(
        int hourOffset,
        double rateUPerHr,
        double durationMinutes
    )
    {
        var mills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();
        var insulin = rateUPerHr * (durationMinutes / 60.0);

        return new Treatment
        {
            Id = $"scheduled-basal-{hourOffset}",
            EventType = "Basal",
            Insulin = insulin,
            Rate = rateUPerHr,
            Duration = durationMinutes,
            Mills = mills,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
        };
    }

    /// <summary>
    /// Creates a bolus treatment at the given hour.
    /// </summary>
    private static Treatment MakeBolus(
        int hourOffset,
        double units,
        string eventType = "Meal Bolus",
        double carbs = 0
    )
    {
        var mills = DayStart.AddHours(hourOffset).ToUnixTimeMilliseconds();

        return new Treatment
        {
            Id = $"bolus-{hourOffset}",
            EventType = eventType,
            Insulin = units,
            Carbs = carbs > 0 ? carbs : null,
            Mills = mills,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
        };
    }

    #endregion

    #region Scheduled Basal Tests

    [Fact]
    public void TDD_WithFlatScheduledBasal_ShouldCalculateCorrectTotal()
    {
        // A flat 1.0 U/hr basal for 24 hours = 24.0 U total basal
        // Delivered as 24 one-hour segments
        var treatments = Enumerable
            .Range(0, 24)
            .Select(h => MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60))
            .ToList();

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        var treatments = new List<Treatment>();

        // Overnight: 0.8 U/hr for 6 hours
        for (var h = 0; h < 6; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 0.8, durationMinutes: 60));

        // Morning: 1.2 U/hr for 4 hours
        for (var h = 6; h < 10; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.2, durationMinutes: 60));

        // Midday: 1.0 U/hr for 6 hours
        for (var h = 10; h < 16; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));

        // Afternoon/Evening: 1.1 U/hr for 6 hours
        for (var h = 16; h < 22; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.1, durationMinutes: 60));

        // Late night: 0.9 U/hr for 2 hours
        for (var h = 22; h < 24; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 0.9, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        // 4.80 + 4.80 + 6.00 + 6.60 + 1.80 = 24.00
        result.TotalBasal.Should().Be(24.0);
        result.Tdd.Should().Be(24.0);
        result.BasalCount.Should().Be(24);
    }

    [Fact]
    public void TDD_WithScheduledBasalAndBoluses_ShouldSumBothCorrectly()
    {
        // Scheduled basal: 0.8 U/hr for 24 hours = 19.2 U
        var treatments = Enumerable
            .Range(0, 24)
            .Select(h => MakeScheduledBasal(h, rateUPerHr: 0.8, durationMinutes: 60))
            .ToList();

        // Add meal boluses throughout the day
        treatments.Add(MakeBolus(7, units: 4.0, carbs: 45)); // Breakfast
        treatments.Add(MakeBolus(12, units: 6.0, carbs: 60)); // Lunch
        treatments.Add(MakeBolus(18, units: 5.5, carbs: 55)); // Dinner

        // Add a correction bolus
        treatments.Add(MakeBolus(15, units: 1.5, eventType: "Correction Bolus"));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        // 0.8 * 24 = 19.2, 4.0 + 6.0 + 5.5 + 1.5 = 17.0, total = 36.2
        result.TotalBasal.Should().Be(19.2);
        result.TotalBolus.Should().Be(17.0);
        result.TotalInsulin.Should().Be(36.2);
        result.Tdd.Should().Be(36.2);
        result.MealBoluses.Should().Be(3);
        result.CorrectionBoluses.Should().Be(1);
        result.BolusCount.Should().Be(4);
    }

    #endregion

    #region Temp Basal Tests

    [Fact]
    public void TDD_WithTempBasalAbsoluteRate_ShouldCalculateFromRate()
    {
        // A single temp basal at 2.0 U/hr for 30 minutes = 1.0 U
        var treatments = new List<Treatment>
        {
            MakeTempBasal(hourOffset: 10, rateUPerHr: 2.0, durationMinutes: 30),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(1.0);
        result.Tdd.Should().Be(1.0);
    }

    [Fact]
    public void TDD_WithTempBasalFromRateOnly_ShouldAutoCalculateInsulin()
    {
        // Temp basal with Rate and Duration but no explicit Insulin.
        // Treatment.Insulin getter will auto-calculate: 1.5 * (60 / 60) = 1.5 U
        var treatments = new List<Treatment>
        {
            MakeTempBasalFromRate(hourOffset: 8, rateUPerHr: 1.5, durationMinutes: 60),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(1.5);
        result.BasalCount.Should().Be(1);
    }

    [Fact]
    public void TDD_WithZeroRateTempBasal_ShouldContributeZeroInsulin()
    {
        // A suspend (zero temp) should contribute 0 insulin
        var treatments = new List<Treatment>
        {
            MakeTempBasal(hourOffset: 3, rateUPerHr: 0.0, durationMinutes: 30),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        var treatments = new List<Treatment>
        {
            MakeTempBasal(0, rateUPerHr: 0.5, durationMinutes: 30), // 0.25 U
            MakeTempBasal(1, rateUPerHr: 1.2, durationMinutes: 30), // 0.60 U
            MakeTempBasal(2, rateUPerHr: 0.0, durationMinutes: 30), // 0.00 U (suspend)
            MakeTempBasal(3, rateUPerHr: 1.8, durationMinutes: 30), // 0.90 U
            MakeTempBasal(4, rateUPerHr: 0.3, durationMinutes: 30), // 0.15 U
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        // The insulin delivered is still the absolute amount (rate * duration / 60),
        // but the percent field is metadata for analysis.
        // Scheduled rate was 1.0 U/hr, temp is 150% = 1.5 U/hr for 60 min = 1.5 U
        var treatments = new List<Treatment>
        {
            MakeTempBasal(hourOffset: 14, rateUPerHr: 1.5, durationMinutes: 60, percent: 150),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        // The uploader should only send the temp basal treatment for that period,
        // not both the scheduled and temp.
        //
        // Schedule: 1.0 U/hr all day (24 segments)
        // Temp at hour 10: 2.0 U/hr for 60 min (replaces the 1.0 U/hr segment)
        //
        // Expected: 23 hours at 1.0 = 23.0 U scheduled + 1 hour at 2.0 = 2.0 U temp = 25.0 U total

        var treatments = new List<Treatment>();

        // Scheduled basal for all hours EXCEPT hour 10 (replaced by temp)
        for (var h = 0; h < 24; h++)
        {
            if (h == 10)
                continue;
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // Temp basal at hour 10: higher rate
        treatments.Add(MakeTempBasal(10, rateUPerHr: 2.0, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        // 23 * 1.0 + 1 * 2.0 = 25.0
        result.TotalBasal.Should().Be(25.0);
        result.Tdd.Should().Be(25.0);
        result.BasalCount.Should().Be(24);
    }

    [Fact]
    public void TDD_TempBasalLowerThanScheduled_ShouldReduceTDD()
    {
        // A reduced temp basal (e.g., pre-exercise) should lower the TDD
        // Schedule: 1.0 U/hr all day
        // Hours 14-16: temp at 0.3 U/hr (2 hours replaced)

        var treatments = new List<Treatment>();

        for (var h = 0; h < 24; h++)
        {
            if (h >= 14 && h < 16)
                continue;
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // 2 hours of reduced temp basal
        treatments.Add(MakeTempBasal(14, rateUPerHr: 0.3, durationMinutes: 60));
        treatments.Add(MakeTempBasal(15, rateUPerHr: 0.3, durationMinutes: 60));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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

        var treatments = new List<Treatment>();

        for (var h = 0; h < 24; h++)
        {
            if (h >= 2 && h < 4)
                continue;
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // 2 hours of zero temp (suspend)
        treatments.Add(MakeTempBasal(2, rateUPerHr: 0.0, durationMinutes: 60, percent: 0));
        treatments.Add(MakeTempBasal(3, rateUPerHr: 0.0, durationMinutes: 60, percent: 0));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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

        var treatments = new List<Treatment>();

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

            treatments.Add(MakeScheduledBasal(h, rate, 60));
        }

        // AID temp basals
        treatments.Add(MakeTempBasal(2, rateUPerHr: 0.0, durationMinutes: 60));
        treatments.Add(MakeTempBasal(7, rateUPerHr: 1.8, durationMinutes: 60));
        treatments.Add(MakeTempBasal(14, rateUPerHr: 0.4, durationMinutes: 60));
        treatments.Add(MakeTempBasal(20, rateUPerHr: 1.3, durationMinutes: 60));

        // Boluses
        treatments.Add(MakeBolus(7, units: 4.5, carbs: 45));
        treatments.Add(MakeBolus(12, units: 5.0, carbs: 60));
        treatments.Add(MakeBolus(18, units: 6.0, carbs: 55));
        treatments.Add(MakeBolus(15, units: 1.2, eventType: "Correction Bolus"));

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        result.MealBoluses.Should().Be(3);
        result.CorrectionBoluses.Should().Be(1);
    }

    [Fact]
    public void TDD_TempBasalWithAbsoluteProperty_ShouldUseAbsoluteRate()
    {
        // Some uploaders use Absolute instead of Rate for temp basals.
        // The Treatment model maps Absolute -> Rate as a fallback,
        // and Insulin auto-calculates from whichever is available.
        var mills = DayStart.AddHours(5).ToUnixTimeMilliseconds();

        var treatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "temp-absolute",
                EventType = "Temp Basal",
                Absolute = 1.6,
                Duration = 30,
                Mills = mills,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
            },
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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
        var treatments = new List<Treatment>();

        // Full day scheduled basal at 1.0 U/hr = 24.0 U basal
        for (var h = 0; h < 24; h++)
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));

        // 16.0 U bolus total
        treatments.Add(MakeBolus(7, units: 5.0, carbs: 45));
        treatments.Add(MakeBolus(12, units: 6.0, carbs: 60));
        treatments.Add(MakeBolus(18, units: 5.0, carbs: 50));

        var result = _statisticsService.CalculateDailyBasalBolusRatios(treatments);

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
        var treatments = new List<Treatment>();

        // Scheduled basal for 22 hours at 1.0 U/hr
        for (var h = 0; h < 24; h++)
        {
            if (h >= 10 && h < 12)
                continue; // replaced by temp
            treatments.Add(MakeScheduledBasal(h, rateUPerHr: 1.0, durationMinutes: 60));
        }

        // Temp basal hours 10-12 at 2.5 U/hr = 5.0 U
        treatments.Add(MakeTempBasal(10, rateUPerHr: 2.5, durationMinutes: 60));
        treatments.Add(MakeTempBasal(11, rateUPerHr: 2.5, durationMinutes: 60));

        var result = _statisticsService.CalculateDailyBasalBolusRatios(treatments);

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

        var treatments = new List<Treatment>();

        // Day 1: flat 1.0 U/hr, no temps = 24.0 U
        for (var h = 0; h < 24; h++)
        {
            var mills = day1Start.AddHours(h).ToUnixTimeMilliseconds();
            treatments.Add(
                new Treatment
                {
                    Id = $"d1-basal-{h}",
                    EventType = "Basal",
                    Insulin = 1.0,
                    Rate = 1.0,
                    Duration = 60,
                    Mills = mills,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
                }
            );
        }

        // Day 2: 20 hours at 1.0 + 4 hours temp at 0.5 = 22.0 U
        for (var h = 0; h < 24; h++)
        {
            var mills = day2Start.AddHours(h).ToUnixTimeMilliseconds();
            if (h >= 8 && h < 12)
            {
                treatments.Add(
                    new Treatment
                    {
                        Id = $"d2-temp-{h}",
                        EventType = "Temp Basal",
                        Insulin = 0.5,
                        Rate = 0.5,
                        Duration = 60,
                        Mills = mills,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
                    }
                );
            }
            else
            {
                treatments.Add(
                    new Treatment
                    {
                        Id = $"d2-basal-{h}",
                        EventType = "Basal",
                        Insulin = 1.0,
                        Rate = 1.0,
                        Duration = 60,
                        Mills = mills,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
                    }
                );
            }
        }

        var result = _statisticsService.CalculateDailyBasalBolusRatios(treatments);

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
        var treatments = new List<Treatment>
        {
            MakeTempBasal(8, rateUPerHr: 1.5, durationMinutes: 30, percent: 150), // High
            MakeTempBasal(10, rateUPerHr: 1.8, durationMinutes: 30, percent: 180), // High
            MakeTempBasal(14, rateUPerHr: 0.5, durationMinutes: 30, percent: 50), // Low
            MakeTempBasal(16, rateUPerHr: 0.0, durationMinutes: 30, percent: 0), // Zero
        };

        var result = _statisticsService.CalculateBasalAnalysis(treatments, StartDate, EndDate);

        result.TempBasalInfo.Total.Should().Be(4);
        result.TempBasalInfo.HighTemps.Should().Be(2);
        // LowTemps counts percent < 100, which includes the zero temp (percent=0)
        result.TempBasalInfo.LowTemps.Should().Be(2);
        result.TempBasalInfo.ZeroTemps.Should().Be(1);
    }

    [Fact]
    public void BasalAnalysis_WithMixedRates_ShouldCalculateCorrectStats()
    {
        var treatments = new List<Treatment>
        {
            MakeScheduledBasal(0, rateUPerHr: 0.8, durationMinutes: 60),
            MakeScheduledBasal(6, rateUPerHr: 1.2, durationMinutes: 60),
            MakeTempBasal(12, rateUPerHr: 1.5, durationMinutes: 60),
            MakeScheduledBasal(18, rateUPerHr: 0.9, durationMinutes: 60),
        };

        var result = _statisticsService.CalculateBasalAnalysis(treatments, StartDate, EndDate);

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
    public void TDD_WithNoTreatments_ShouldReturnZeros()
    {
        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            Array.Empty<Treatment>(),
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
        var treatments = new List<Treatment>
        {
            MakeBolus(7, units: 5.0, carbs: 45),
            MakeBolus(12, units: 6.0, carbs: 60),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
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

        var treatments = new List<Treatment>();
        for (var d = 0; d < 3; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                var mills = DayStart.AddDays(d).AddHours(h).ToUnixTimeMilliseconds();
                treatments.Add(
                    new Treatment
                    {
                        Id = $"basal-d{d}-h{h}",
                        EventType = "Basal",
                        Insulin = 1.0,
                        Rate = 1.0,
                        Duration = 60,
                        Mills = mills,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
                    }
                );
            }
        }

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(treatments, start, end);

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
        var treatments = new List<Treatment>
        {
            MakeTempBasal(hourOffset: 10, rateUPerHr: 3.0, durationMinutes: 5),
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        result.TotalBasal.Should().Be(0.25);
    }

    [Fact]
    public void TDD_SMBTreatments_ShouldBeCountedAsBolus()
    {
        // Super Micro Boluses from AID systems should be counted as bolus, not basal
        var mills = DayStart.AddHours(10).ToUnixTimeMilliseconds();

        var treatments = new List<Treatment>
        {
            new Treatment
            {
                Id = "smb-1",
                EventType = "SMB",
                Insulin = 0.3,
                Mills = mills,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("o"),
            },
        };

        var result = _statisticsService.CalculateInsulinDeliveryStatistics(
            treatments,
            StartDate,
            EndDate
        );

        result.TotalBolus.Should().Be(0.3);
        result.TotalBasal.Should().Be(0);
        result.BolusCount.Should().Be(1);
    }

    #endregion
}
