using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Tests for Total Daily Dose (TDD) and insulin delivery calculations.
/// Ensures consistency across all calculation paths:
///   - CalculateTreatmentSummary (v4: Bolus + CarbIntake)
///   - CalculateInsulinDeliveryStatistics (v4: Bolus + TempBasal)
///   - CalculateDailyBasalBolusRatios (v4: Bolus + TempBasal)
///   - GetTotalInsulin / GetBolusPercentage / GetBasalPercentage
/// </summary>
public class StatisticsServiceTDDTests
{
    private readonly StatisticsService _sut;

    // Standard date range for tests — exactly 7 days
    private static readonly DateTime StartDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndDate = new(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long Day1Mills = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long Day2Mills = new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long Day3Mills = new DateTimeOffset(2024, 1, 3, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public StatisticsServiceTDDTests()
    {
        _sut = new StatisticsService();
    }

    #region CalculateTreatmentSummary — Basic Scenarios

    [Fact]
    public void TreatmentSummary_BolusOnly_ShouldSumCorrectly()
    {
        var boluses = new[]
        {
            MakeBolus(4.0),
            MakeBolus(2.5),
            MakeBolus(0.3, automatic: true), // SMB
        };

        var result = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());

        result.Totals.Insulin.Bolus.Should().Be(6.8);
        result.Totals.Insulin.Basal.Should().Be(0);
    }

    [Fact]
    public void TreatmentSummary_CarbsAndMacros_ShouldAggregate()
    {
        var boluses = new[]
        {
            MakeBolus(4.0),
            MakeBolus(1.0),
        };
        var carbIntakes = new[]
        {
            new CarbIntake { Carbs = 45 },
            new CarbIntake { Carbs = 15 },
        };

        var result = _sut.CalculateTreatmentSummary(boluses, carbIntakes);

        result.Totals.Food.Carbs.Should().Be(60);
        result.CarbToInsulinRatio.Should().Be(12, "60g / 5U = 12");
    }

    #endregion

    #region CalculateInsulinDeliveryStatistics — Basic Scenarios

    [Fact]
    public void InsulinDelivery_BolusOnly_ShouldCalculateCorrectly()
    {
        var boluses = new[]
        {
            MakeBolus(4.0, Day1Mills),
            MakeBolus(2.5, Day1Mills),
            MakeBolus(0.3, Day1Mills, automatic: true), // SMB
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate);

        result.TotalBolus.Should().Be(6.8);
        result.TotalBasal.Should().Be(0);
        result.TotalInsulin.Should().Be(6.8);
        result.BolusCount.Should().Be(3);
    }

    [Fact]
    public void InsulinDelivery_BasalFromTempBasals_ShouldCalculateCorrectly()
    {
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills),                       // 0.5U
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day1Mills + 30 * 60_000L), // 0.8U
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        result.TotalBasal.Should().Be(1.3);
        result.TotalBolus.Should().Be(0);
        result.TotalInsulin.Should().Be(1.3);
    }

    [Fact]
    public void InsulinDelivery_TDD_ShouldDivideByDateRangeSpan()
    {
        // 7-day range, 70U total -> TDD should be 10U/day
        var boluses = new[]
        {
            MakeBolus(35.0, Day1Mills),
        };
        var tempBasals = new[]
        {
            // 35 hours at 1.0 U/hr = 35U
            MakeTempBasal(rate: 1.0, durationMinutes: 60 * 35, mills: Day1Mills),
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        result.Tdd.Should().Be(10.0, "70U / 7 days = 10 U/day");
        result.DayCount.Should().Be(7);
    }

    #endregion

    #region Consistency: TreatmentSummary vs InsulinDeliveryStatistics

    [Fact]
    public void Consistency_SameData_BolusInsulinShouldMatch()
    {
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills),
            MakeBolus(2.0, Day1Mills),
            MakeBolus(0.5, Day2Mills, automatic: true), // SMB
        };
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeTempBasal(rate: 0.5, durationMinutes: 120, mills: Day2Mills), // 1.0U
        };

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        // TreatmentSummary in v4 only tracks bolus (basal comes from TempBasals)
        summary.Totals.Insulin.Bolus.Should().Be(delivery.TotalBolus,
            "TreatmentSummary and InsulinDeliveryStatistics should agree on bolus insulin");
    }

    [Fact]
    public void Consistency_SameData_BolusAndBasalShouldMatch()
    {
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills),
            MakeBolus(2.0, Day2Mills),
        };
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeTempBasal(rate: 0.5, durationMinutes: 120, mills: Day2Mills), // 1.0U
        };

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        summary.Totals.Insulin.Bolus.Should().Be(delivery.TotalBolus,
            "bolus totals should match between methods");
        // In v4, TreatmentSummary does not track basal (that's from TempBasals)
        delivery.TotalBasal.Should().Be(2.0,
            "basal total should come from TempBasals in InsulinDeliveryStatistics");
    }

    [Fact]
    public void Consistency_DailyRatios_TotalShouldMatchDelivery()
    {
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills),
            MakeBolus(2.0, Day1Mills),
            MakeBolus(4.0, Day2Mills),
        };
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day2Mills), // 0.8U
        };

        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals);

        var ratiosGrandTotal = ratios.DailyData.Sum(d => d.Total);

        ratiosGrandTotal.Should().Be(delivery.TotalInsulin,
            "daily ratios grand total should match delivery total");
    }

    [Fact]
    public void Consistency_PercentagesShouldSumTo100()
    {
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills),
        };
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
        };

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        (delivery.BolusPercent + delivery.BasalPercent).Should().BeApproximately(100.0, 0.1,
            "InsulinDelivery basal% + bolus% should sum to 100");
    }

    #endregion

    #region Realistic AID System Patterns

    [Fact]
    public void RealisticPattern_AndroidAPS_SMBsAndTempBasals_ShouldSumCorrectly()
    {
        // AndroidAPS pattern: many SMBs (small boluses) + frequent temp basals
        var boluses = new List<Bolus>();
        var tempBasals = new List<TempBasal>();
        var baseMills = Day1Mills;

        // 24 temp basals over the day (one per hour), varying rates
        for (int hour = 0; hour < 24; hour++)
        {
            var mills = baseMills + (hour * 3600000L);
            tempBasals.Add(MakeTempBasal(
                rate: 0.5 + (hour % 4) * 0.2, // Varies 0.5-1.1 U/hr
                durationMinutes: 60,
                mills: mills
            ));
        }

        // 8 SMBs throughout the day (typical for loop system)
        for (int i = 0; i < 8; i++)
        {
            var mills = baseMills + ((i * 3 + 1) * 3600000L); // Every 3 hours
            boluses.Add(MakeBolus(0.3 + (i % 3) * 0.2, mills, automatic: true));
        }

        // 3 meal boluses
        boluses.Add(MakeBolus(4.5, baseMills + 7 * 3600000L));  // breakfast
        boluses.Add(MakeBolus(6.0, baseMills + 12 * 3600000L)); // lunch
        boluses.Add(MakeBolus(5.5, baseMills + 18 * 3600000L)); // dinner

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        // Bolus total from TreatmentSummary should match delivery bolus total
        summary.Totals.Insulin.Bolus.Should().Be(delivery.TotalBolus,
            "AID pattern: summary bolus and delivery bolus totals should match");

        // Bolus should be meal boluses + SMBs
        delivery.TotalBolus.Should().BeGreaterThan(0);
        delivery.TotalBasal.Should().BeGreaterThan(0);
        delivery.BolusCount.Should().Be(11, "3 meals + 8 SMBs = 11 boluses");
    }

    #region LoopSystem Overlap

    [Fact]
    public void LoopSystem_OverlappingTempBasals_ShouldClipToActualDelivery()
    {
        // Trio pattern: 12 records at 1.0 U/hr, 30-min declared duration, every 5 min.
        // Naive:  12 × 0.5 hr × 1.0 = 6.0 U.
        // Clipped: 11 × (5/60) hr × 1.0 + 1 × 0.5 hr × 1.0 = 0.917 + 0.5 = 1.417 U
        //          (last record keeps its declared 30-min end — see Task 4 follow-up bug)
        var tempBasals = Enumerable.Range(0, 12)
            .Select(i => MakeTempBasal(
                rate: 1.0,
                durationMinutes: 30,
                mills: Day1Mills + i * 5 * 60_000L))
            .ToArray();

        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals,
            Array.Empty<CarbIntake>(), StartDate, EndDate);

        var ratios = _sut.CalculateDailyBasalBolusRatios(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals);

        delivery.TotalBasal.Should().BeApproximately(1.417, 0.01,
            "11 clipped to 5-min windows + last record at full 30 min = 1.417 U, not 6.0 U (naive)");
        ratios.DailyData.Sum(d => d.Basal).Should().BeApproximately(1.417, 0.01,
            "daily ratios should also use clipped durations");
    }

    [Fact]
    public void LoopSystem_VaryingRates_ShouldUseEachRecordsOwnRate()
    {
        // Alternating 0.5 and 2.0 U/hr, every 5 min, 30-min declared duration.
        // Last record (index 11) is 2.0 U/hr and keeps its declared 30-min end.
        // Clipped: 6 × 0.5 × (5/60) + 5 × 2.0 × (5/60) + 1 × 2.0 × 0.5 = 0.25 + 0.833 + 1.0 = 2.083 U
        var tempBasals = Enumerable.Range(0, 12)
            .Select(i => MakeTempBasal(
                rate: i % 2 == 0 ? 0.5 : 2.0,
                durationMinutes: 30,
                mills: Day1Mills + i * 5 * 60_000L))
            .ToArray();

        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals,
            Array.Empty<CarbIntake>(), StartDate, EndDate);

        delivery.TotalBasal.Should().BeApproximately(2.083, 0.01,
            "each clipped window uses its own record's rate; naive would be 7.5 U");
    }

    #endregion

    [Fact]
    public void RealisticPattern_Omnipod_BolusOnlyTreatments_ShouldReportBolusCorrectly()
    {
        // Omnipod pattern: only bolus treatments uploaded (basal is not in treatments)
        var boluses = new[]
        {
            MakeBolus(4.0, Day1Mills),
            MakeBolus(1.5, Day1Mills + 3600000),
            MakeBolus(5.5, Day1Mills + 7 * 3600000L),
        };

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate);

        // Only bolus insulin should be reported
        summary.Totals.Insulin.Bolus.Should().Be(11.0);
        summary.Totals.Insulin.Basal.Should().Be(0,
            "no TempBasals -- basal should be 0");
        delivery.TotalBolus.Should().Be(11.0);
        delivery.TotalBasal.Should().Be(0);

        // Percentages: 100% bolus, 0% basal (because no basal data)
        delivery.BolusPercent.Should().Be(100.0);
        delivery.BasalPercent.Should().Be(0);
    }

    [Fact]
    public void RealisticPattern_MDI_SeparateBasalAndBolus_ShouldSumCorrectly()
    {
        // MDI (Multiple Daily Injections) pattern:
        // One long-acting basal injection + multiple rapid-acting boluses
        // In v4, long-acting basal is represented as a TempBasal covering the day
        var tempBasals = new[]
        {
            // Long-acting basal (e.g., Lantus 20U once daily)
            // Modeled as a 24-hour TempBasal: rate = 20/24 U/hr over 1440 minutes
            MakeTempBasal(rate: 20.0 / 24.0, durationMinutes: 1440, mills: Day1Mills),
        };
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills + 7 * 3600000L),
            MakeBolus(7.0, Day1Mills + 12 * 3600000L),
            MakeBolus(6.0, Day1Mills + 18 * 3600000L),
            MakeBolus(2.0, Day1Mills + 21 * 3600000L),
        };

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        // Basal comes from TempBasal: 20/24 U/hr * 24 hr = 20U
        delivery.TotalBasal.Should().BeApproximately(20.0, 0.01, "long-acting basal = 20U via TempBasal");
        summary.Totals.Insulin.Bolus.Should().Be(20.0, "meal + correction boluses = 20U");

        delivery.TotalBolus.Should().Be(20.0);
        delivery.TotalInsulin.Should().BeApproximately(40.0, 0.01);

        // ~50/50 split
        delivery.BolusPercent.Should().BeApproximately(50.0, 0.5);
        delivery.BasalPercent.Should().BeApproximately(50.0, 0.5);
    }

    #endregion

    #region DailyBasalBolusRatios

    [Fact]
    public void DailyRatios_MultiDayData_ShouldGroupByDay()
    {
        var boluses = new[]
        {
            MakeBolus(5.0, Day1Mills),
            MakeBolus(3.0, Day2Mills),
        };
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day2Mills), // 0.8U
        };

        var result = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals);

        result.DayCount.Should().Be(2);
        result.DailyData.Should().HaveCount(2);

        // Day 1: 5.0 bolus + 1.0 basal = 6.0 total
        result.DailyData[0].Bolus.Should().Be(5.0);
        result.DailyData[0].Basal.Should().Be(1.0);
        result.DailyData[0].Total.Should().Be(6.0);

        // Day 2: 3.0 bolus + 0.8 basal = 3.8 total
        result.DailyData[1].Bolus.Should().Be(3.0);
        result.DailyData[1].Basal.Should().Be(0.8);
        result.DailyData[1].Total.Should().Be(3.8);
    }

    [Fact]
    public void DailyRatios_AverageTdd_ShouldDivideByDaysWithData()
    {
        var boluses = new[]
        {
            MakeBolus(10.0, Day1Mills),
            MakeBolus(20.0, Day2Mills),
        };

        var result = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>());

        // AverageTdd divides by DayCount (days with data), not date range
        result.DayCount.Should().Be(2);
        result.AverageTdd.Should().Be(15.0, "30U / 2 days with data = 15 U/day");
    }

    #endregion

    #region TDD DayCount Consistency Documentation

    /// <summary>
    /// DOCUMENTS DESIGN DIFFERENCE: CalculateInsulinDeliveryStatistics divides by
    /// date range span, while CalculateDailyBasalBolusRatios divides by days with data.
    /// Both are valid but produce different results when data is sparse.
    /// </summary>
    [Fact]
    public void TDD_DayCountDifference_ShouldBeDocumented()
    {
        // Boluses on only 2 out of 7 days
        var boluses = new[]
        {
            MakeBolus(35.0, Day1Mills),
            MakeBolus(35.0, Day2Mills),
        };

        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>());

        // InsulinDelivery: 70U / 7 days = 10 U/day
        delivery.DayCount.Should().Be(7);
        delivery.Tdd.Should().Be(10.0);

        // DailyRatios: 70U / 2 days = 35 U/day
        ratios.DayCount.Should().Be(2);
        ratios.AverageTdd.Should().Be(35.0);

        // These INTENTIONALLY differ -- document this design choice:
        // InsulinDelivery.Tdd is better for "average across the period"
        // DailyRatios.AverageTdd is better for "average on days with insulin"
    }

    #endregion

    #region OverallAverages TDD

    [Fact]
    public void OverallAverages_TDD_ShouldUseGetTotalInsulin()
    {
        var dayData = new[]
        {
            new DayData
            {
                Date = "2024-01-01",
                TreatmentSummary = new TreatmentSummary
                {
                    Totals = new TreatmentTotals
                    {
                        Insulin = new InsulinTotals { Bolus = 15, Basal = 10 },
                        Food = new FoodTotals(),
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages(),
                },
            },
            new DayData
            {
                Date = "2024-01-02",
                TreatmentSummary = new TreatmentSummary
                {
                    Totals = new TreatmentTotals
                    {
                        Insulin = new InsulinTotals { Bolus = 20, Basal = 15 },
                        Food = new FoodTotals(),
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages(),
                },
            },
        };

        var result = _sut.CalculateOverallAverages(dayData);

        // Day 1: 25U, Day 2: 35U -> Avg = 30 U/day
        result!.AvgTotalDaily.Should().Be(30.0);
        result.AvgBolus.Should().Be(17.5);  // (15 + 20) / 2
        result.AvgBasal.Should().Be(12.5);  // (10 + 15) / 2
    }

    [Fact]
    public void OverallAverages_PercentagesShouldBeConsistent()
    {
        var dayData = new[]
        {
            new DayData
            {
                Date = "2024-01-01",
                TreatmentSummary = new TreatmentSummary
                {
                    Totals = new TreatmentTotals
                    {
                        Insulin = new InsulinTotals { Bolus = 10, Basal = 10 },
                        Food = new FoodTotals(),
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages(),
                },
            },
        };

        var result = _sut.CalculateOverallAverages(dayData);

        result!.BolusPercentage.Should().Be(50.0);
        result.BasalPercentage.Should().Be(50.0);
        (result.BolusPercentage + result.BasalPercentage).Should().Be(100.0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyCollections_AllMethods_ShouldReturnZeroes()
    {
        var emptyBoluses = Array.Empty<Bolus>();
        var emptyTempBasals = Array.Empty<TempBasal>();
        var emptyAlgorithmBoluses = Array.Empty<Bolus>();

        var summary = _sut.CalculateTreatmentSummary(emptyBoluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            emptyBoluses, emptyAlgorithmBoluses, emptyTempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(emptyBoluses, emptyAlgorithmBoluses, emptyTempBasals);

        _sut.GetTotalInsulin(summary).Should().Be(0);
        delivery.TotalInsulin.Should().Be(0);
        delivery.Tdd.Should().Be(0);
        ratios.AverageTdd.Should().Be(0);
    }

    [Fact]
    public void VerySmallInsulinAmounts_ShouldNotBeLost()
    {
        // SMBs can be very small (0.05U increments on some pumps)
        var boluses = Enumerable.Range(0, 20)
            .Select(i => MakeBolus(0.05, Day1Mills + i * 300000L, automatic: true))
            .ToArray();

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate);

        // 20 * 0.05 = 1.0U
        summary.Totals.Insulin.Bolus.Should().BeApproximately(1.0, 0.001,
            "many small SMBs should sum correctly without floating-point drift");
        delivery.TotalBolus.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void FormatInsulinDisplay_ShouldFormatCorrectly()
    {
        _sut.FormatInsulinDisplay(1.0).Should().Be("1.00");
        // Values < 1 use "shifted" format (no leading zero)
        _sut.FormatInsulinDisplay(0.05).Should().Be(".05");
        _sut.FormatInsulinDisplay(10.567).Should().Be("10.57");
    }

    [Fact]
    public void GetBolusPercentage_WithZeroTotal_ShouldReturnZero()
    {
        var summary = new TreatmentSummary
        {
            Totals = new TreatmentTotals
            {
                Insulin = new InsulinTotals { Bolus = 0, Basal = 0 },
            },
        };

        _sut.GetBolusPercentage(summary).Should().Be(0);
        _sut.GetBasalPercentage(summary).Should().Be(0);
    }

    [Fact]
    public void Suspension_TempBasal_ShouldNotAddInsulin()
    {
        // Suspension = Rate 0 for a duration -> 0 insulin
        var tempBasals = new[]
        {
            MakeTempBasal(rate: 0.0, durationMinutes: 30, mills: Day1Mills),
        };

        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);

        delivery.TotalBasal.Should().Be(0);
    }

    #endregion

    #region Multi-Day Realistic Scenario

    [Fact]
    public void SevenDayRealisticScenario_AllMethodsShouldBeConsistent()
    {
        var boluses = new List<Bolus>();
        var tempBasals = new List<TempBasal>();
        var dailyExpectedBolus = new double[] { 18.0, 22.0, 15.0, 20.0, 19.0, 21.0, 17.0 };
        var dailyExpectedBasal = new double[] { 24.0, 24.0, 23.5, 24.0, 22.0, 24.5, 24.0 };

        for (int day = 0; day < 7; day++)
        {
            var dayMills = new DateTimeOffset(2024, 1, 1 + day, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

            // 3 meal boluses per day
            boluses.Add(MakeBolus(dailyExpectedBolus[day] * 0.4, dayMills));
            boluses.Add(MakeBolus(dailyExpectedBolus[day] * 0.35, dayMills + 4 * 3600000L));
            boluses.Add(MakeBolus(dailyExpectedBolus[day] * 0.25, dayMills + 10 * 3600000L));

            // 24 hourly temp basals per day
            for (int hour = 0; hour < 24; hour++)
            {
                var hourMills = new DateTimeOffset(2024, 1, 1 + day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                tempBasals.Add(MakeTempBasal(
                    rate: dailyExpectedBasal[day] / 24.0, // Even hourly rate
                    durationMinutes: 60,
                    mills: hourMills
                ));
            }
        }

        var summary = _sut.CalculateTreatmentSummary(boluses, Array.Empty<CarbIntake>());
        var delivery = _sut.CalculateInsulinDeliveryStatistics(
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals);

        var expectedTotalBolus = dailyExpectedBolus.Sum();
        var expectedTotalBasal = dailyExpectedBasal.Sum();
        var expectedTotal = expectedTotalBolus + expectedTotalBasal;

        // All methods should agree on totals (within rounding tolerance)
        delivery.TotalInsulin.Should().BeApproximately(expectedTotal, 0.1);

        var ratiosTotal = ratios.DailyData.Sum(d => d.Total);
        ratiosTotal.Should().BeApproximately(expectedTotal, 1.0); // Slightly more rounding from per-day rounding

        // TDD should be ~45U/day
        var expectedAvgTdd = expectedTotal / 7.0;
        delivery.Tdd.Should().BeApproximately(expectedAvgTdd, 0.5);
    }

    #endregion

    #region ClipOverlappingTempBasals

    [Fact]
    public void Clip_NonOverlapping_ShouldBeUnchanged()
    {
        // 3 hourly records, perfectly adjacent — clip is a no-op
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills),
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day1Mills + 3_600_000L),
            MakeTempBasal(rate: 1.2, durationMinutes: 60, mills: Day1Mills + 7_200_000L),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records);

        clipped.Should().HaveCount(3);
        for (int i = 0; i < clipped.Count; i++)
            clipped[i].EffectiveEndMills.Should().Be(records[i].EndMills!.Value,
                $"non-overlapping record {i} should be unchanged");
    }

    [Fact]
    public void Clip_LastRecord_ShouldUseOriginalEnd()
    {
        var record = MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills);

        var clipped = StatisticsService.ClipOverlappingTempBasals(new[] { record });

        clipped.Should().HaveCount(1);
        clipped[0].EffectiveEndMills.Should().Be(record.EndMills!.Value);
    }

    [Fact]
    public void Clip_Overlapping_ShouldTruncateToNextStart()
    {
        // 3 records starting every 5 min, each with 30-min duration
        var t0 = Day1Mills;
        var t1 = Day1Mills + 5 * 60_000L;
        var t2 = Day1Mills + 10 * 60_000L;
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t0),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t1),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t2),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records);

        clipped.Should().HaveCount(3);
        clipped[0].EffectiveEndMills.Should().Be(t1, "first record clipped to second's start");
        clipped[1].EffectiveEndMills.Should().Be(t2, "second record clipped to third's start");
        clipped[2].EffectiveEndMills.Should().Be(records[2].EndMills!.Value, "last record unchanged");
    }

    [Fact]
    public void Clip_MultipleDeviceGroups_ShouldClipWithinGroupOnly()
    {
        // Device A: one 60-min record at t0
        // Device B: one 60-min record at t0 + 5 min
        // Device A's record must NOT be clipped by Device B's record
        var t0 = Day1Mills;
        var tB = Day1Mills + 5 * 60_000L;

        var recordA = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(t0).UtcDateTime,
            EndTimestamp   = DateTimeOffset.FromUnixTimeMilliseconds(t0 + 60 * 60_000L).UtcDateTime,
            Rate           = 1.0,
            Origin         = TempBasalOrigin.Algorithm,
            Device         = "PumpA",
        };
        var recordB = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(tB).UtcDateTime,
            EndTimestamp   = DateTimeOffset.FromUnixTimeMilliseconds(tB + 60 * 60_000L).UtcDateTime,
            Rate           = 1.0,
            Origin         = TempBasalOrigin.Algorithm,
            Device         = "PumpB",
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(new[] { recordA, recordB });

        clipped.Should().HaveCount(2);
        var a = clipped.Single(c => c.Tb.Device == "PumpA");
        var b = clipped.Single(c => c.Tb.Device == "PumpB");

        a.EffectiveEndMills.Should().Be(recordA.EndMills!.Value,
            "device A is not clipped by device B");
        b.EffectiveEndMills.Should().Be(recordB.EndMills!.Value,
            "device B is not clipped by device A");
    }

    [Fact]
    public void Clip_ExactSameStart_ZeroDuration()
    {
        // Two records with identical start (exact cross-source duplicate)
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills), // same device (null), same start
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records);

        clipped.Should().HaveCount(2);
        // Whichever sorts first gets EffectiveEnd = Day1Mills (zero duration)
        clipped.Should().Contain(c => c.EffectiveEndMills == c.Tb.StartMills,
            "one duplicate collapses to zero duration");
    }

    #endregion

    #region Helper Methods

    private static Bolus MakeBolus(double insulin, long? mills = null, bool automatic = false)
    {
        return new Bolus
        {
            Insulin = insulin,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills ?? Day1Mills).UtcDateTime,
            Automatic = automatic,
        };
    }

    private static TempBasal MakeTempBasal(double rate, double durationMinutes, long? mills = null)
    {
        var startMills = mills ?? Day1Mills;
        return new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startMills + (long)(durationMinutes * 60 * 1000)).UtcDateTime,
            Rate = rate,
            Origin = TempBasalOrigin.Algorithm,
        };
    }

    #endregion
}
