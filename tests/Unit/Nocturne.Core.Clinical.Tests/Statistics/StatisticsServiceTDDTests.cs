using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using static Nocturne.Core.Clinical.Tests.Statistics.WindowEnd;

namespace Nocturne.Core.Clinical.Tests.Statistics;

/// <summary>
/// Tests for Total Daily Dose (TDD) and insulin delivery calculations.
/// Ensures consistency across all calculation paths:
///   - CalculateTreatmentSummary (v4: Bolus + CarbIntake)
///   - CalculateInsulinDeliveryStatistics (v4: Bolus + TempBasal)
///   - CalculateDailyBasalBolusRatios (v4: Bolus + TempBasal)
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

    [Fact]
    public void TreatmentSummary_AveragePerBolus_IsBolusInsulinOverBolusCount()
    {
        var boluses = new[]
        {
            MakeBolus(4.0),
            MakeBolus(2.0),
            MakeBolus(0.5, automatic: true), // SMB, still a bolus record
        };
        var carbIntakes = new[] { new CarbIntake { Carbs = 45 } };

        var result = _sut.CalculateTreatmentSummary(boluses, carbIntakes);

        result.BolusCount.Should().Be(3);
        result.CarbEntryCount.Should().Be(1);
        result.AveragePerBolus.Should().Be(6.5 / 3);
        result.AverageCarbsPerEntry.Should().Be(45);
    }

    [Fact]
    public void TreatmentSummary_PerDayAverages_DivideByTheDayCount()
    {
        var boluses = Enumerable.Range(0, 6).Select(_ => MakeBolus(2.0)).ToArray();
        var carbIntakes = Enumerable.Range(0, 2).Select(_ => new CarbIntake { Carbs = 45 }).ToArray();

        var result = _sut.CalculateTreatmentSummary(boluses, carbIntakes, dayCount: 6);

        result.DailyBoluses.Should().Be(1.0);
        result.DailyCarbs.Should().Be(15.0);
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
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        result.TotalBolus.Should().Be(6.8);
        result.TotalBasal.Should().Be(0);
        result.TotalInsulin.Should().Be(6.8);
        result.ManualBolusCount.Should().Be(3);
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
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        result.Tdd.Should().Be(10.0, "70U / 7 days = 10 U/day");
        result.WindowDays.Should().Be(7);
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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        // Bolus total from TreatmentSummary should match delivery bolus total
        summary.Totals.Insulin.Bolus.Should().Be(delivery.TotalBolus,
            "AID pattern: summary bolus and delivery bolus totals should match");

        // Bolus should be meal boluses + SMBs
        delivery.TotalBolus.Should().BeGreaterThan(0);
        delivery.TotalBasal.Should().BeGreaterThan(0);
        delivery.ManualBolusCount.Should().Be(11, "the 8 automatic-flagged records are stored as manual until #2412, so 3 meals + 8 = 11");
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
            Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        var ratios = _sut.CalculateDailyBasalBolusRatios(
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals, Mills(EndDate));

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
            Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        // Only bolus insulin should be reported
        summary.Totals.Insulin.Bolus.Should().Be(11.0);
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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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

        var result = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals, Mills(EndDate));

        result.DaysWithData.Should().Be(2);
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

        var result = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Mills(EndDate));

        // AverageTdd divides by DaysWithData (days with data), not date range
        result.DaysWithData.Should().Be(2);
        result.AverageTdd.Should().Be(15.0, "30U / 2 days with data = 15 U/day");
    }

    [Fact]
    public void DailyRatios_Carbs_ShouldSumPerDayWithoutChangingTheInsulinAverages()
    {
        var boluses = new[]
        {
            MakeBolus(10.0, Day1Mills),
            MakeBolus(20.0, Day2Mills),
        };
        var carbs = new[]
        {
            MakeCarbIntake(45.0, Day1Mills),
            MakeCarbIntake(30.5, Day1Mills + 3_600_000),
            MakeCarbIntake(60.0, Day3Mills), // carbs logged, no insulin
            MakeCarbIntake(0, Day2Mills),
        };

        var result = _sut.CalculateDailyBasalBolusRatios(
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Mills(EndDate), carbIntakes: carbs);

        result.DailyData.Select(d => (d.Date, d.Carbs)).Should().Equal(
            ("2024-01-01", 75.5),
            ("2024-01-02", 0.0),
            ("2024-01-03", 60.0));
        result.DailyData.Single(d => d.Date == "2024-01-03").Total.Should().Be(0);
        result.DaysWithData.Should().Be(2, "a carb-only day is not a day with insulin");
        result.AverageTdd.Should().Be(15.0);
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
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Mills(EndDate));

        // InsulinDelivery: 70U / 7 days = 10 U/day
        delivery.WindowDays.Should().Be(7);
        delivery.Tdd.Should().Be(10.0);

        // DailyRatios: 70U / 2 days = 35 U/day
        ratios.DaysWithData.Should().Be(2);
        ratios.AverageTdd.Should().Be(35.0);

        // These INTENTIONALLY differ -- document this design choice:
        // InsulinDelivery.Tdd is better for "average across the period"
        // DailyRatios.AverageTdd is better for "average on days with insulin"
    }

    #endregion

    #region OverallAverages TDD

    [Fact]
    public void OverallAverages_ShouldAverageBolusOverDaysWithBolus()
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
                        Insulin = new InsulinTotals { Bolus = 15 },
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
                        Insulin = new InsulinTotals { Bolus = 20 },
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

        result!.AvgBolus.Should().Be(17.5);
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
            emptyBoluses, emptyAlgorithmBoluses, emptyTempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));
        var ratios = _sut.CalculateDailyBasalBolusRatios(emptyBoluses, emptyAlgorithmBoluses, emptyTempBasals, Mills(EndDate));

        summary.Totals.Insulin.Bolus.Should().Be(0);
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
            boluses, Array.Empty<Bolus>(), Array.Empty<TempBasal>(), Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

        // 20 * 0.05 = 1.0U
        summary.Totals.Insulin.Bolus.Should().BeApproximately(1.0, 0.001,
            "many small SMBs should sum correctly without floating-point drift");
        delivery.TotalBolus.Should().BeApproximately(1.0, 0.01);
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
            Array.Empty<Bolus>(), Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));

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
            boluses, Array.Empty<Bolus>(), tempBasals, Array.Empty<CarbIntake>(), StartDate, EndDate, Mills(EndDate));
        var ratios = _sut.CalculateDailyBasalBolusRatios(boluses, Array.Empty<Bolus>(), tempBasals, Mills(EndDate));

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

    private const long Minute = 60_000L;
    private static readonly long Now = Day1Mills + 12 * 60 * Minute;

    [Fact]
    public void Clip_NonOverlapping_ShouldBeUnchanged()
    {
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills),
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day1Mills + 3_600_000L),
            MakeTempBasal(rate: 1.2, durationMinutes: 60, mills: Day1Mills + 7_200_000L),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records, Now);

        Spans(clipped).Should().Equal(records.Select(r => (r.StartMills, r.EndMills!.Value)));
    }

    [Fact]
    public void Clip_LastRecord_ShouldUseOriginalEnd()
    {
        var record = MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills);

        var clipped = StatisticsService.ClipOverlappingTempBasals(new[] { record }, Now);

        Spans(clipped).Should().Equal((record.StartMills, record.EndMills!.Value));
    }

    [Fact]
    public void Clip_RunningRecordWithNothingAfterIt_RunsToNow()
    {
        var running = Running(Day1Mills);

        var clipped = StatisticsService.ClipOverlappingTempBasals([running], Now);

        Spans(clipped).Should().Equal((Day1Mills, Now));
    }

    [Fact]
    public void Clip_RunningRecord_RunsToTheNextRecordOfItsDevice()
    {
        var running = Running(Day1Mills);
        var next = MakeTempBasal(rate: 0.5, durationMinutes: 30, mills: Day1Mills + 90 * Minute);

        var clipped = StatisticsService.ClipOverlappingTempBasals([running, next], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 90 * Minute),
            (Day1Mills + 90 * Minute, Day1Mills + 120 * Minute));
    }

    [Fact]
    public void Clip_RunningRecordThatStartsAfterNow_DeliversNothing()
    {
        StatisticsService.ClipOverlappingTempBasals([Running(Now + Minute)], Now).Should().BeEmpty();
    }

    [Fact]
    public void Clip_Cancel_EndsTheTempBeforeItAndThenTheScheduleRuns()
    {
        var temp = MakeTempBasal(rate: 2.0, durationMinutes: 30, mills: Day1Mills);
        var cancel = Cancel(Day1Mills + 10 * Minute);

        var clipped = StatisticsService.ClipOverlappingTempBasals([temp, cancel], Now);

        Spans(clipped).Should().Equal((Day1Mills, Day1Mills + 10 * Minute));
    }

    [Fact]
    public void Clip_CancelAtTheInstantANewTempStarts_KeepsTheNewTemp()
    {
        var temp = MakeTempBasal(rate: 2.0, durationMinutes: 30, mills: Day1Mills + 10 * Minute);
        var cancel = Cancel(Day1Mills + 10 * Minute);

        var clipped = StatisticsService.ClipOverlappingTempBasals([temp, cancel], Now);

        Spans(clipped).Should().Equal((Day1Mills + 10 * Minute, Day1Mills + 40 * Minute));
    }

    [Fact]
    public void Clip_Overlapping_ShouldTruncateToNextStart()
    {
        var t0 = Day1Mills;
        var t1 = Day1Mills + 5 * Minute;
        var t2 = Day1Mills + 10 * Minute;
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t0),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t1),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: t2),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records, Now);

        Spans(clipped).Should().Equal((t0, t1), (t1, t2), (t2, t2 + 30 * Minute));
    }

    [Fact]
    public void Clip_ExactSameStartInOneStream_CountsOnce()
    {
        var records = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals(records, Now);

        Spans(clipped).Should().Equal((Day1Mills, Day1Mills + 30 * Minute));
    }

    [Fact]
    public void Clip_StreamsThatDoNotOverlap_AreBothKeptWhole()
    {
        var loop = Stream("openaps://Trio", Day1Mills, count: 3, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = Stream("tidepool", Day1Mills + 60 * Minute, count: 3, everyMinutes: 5, durationMinutes: 30);

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. loop, .. mirror], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 5 * Minute),
            (Day1Mills + 5 * Minute, Day1Mills + 10 * Minute),
            (Day1Mills + 10 * Minute, Day1Mills + 40 * Minute),
            (Day1Mills + 60 * Minute, Day1Mills + 65 * Minute),
            (Day1Mills + 65 * Minute, Day1Mills + 70 * Minute),
            (Day1Mills + 70 * Minute, Day1Mills + 100 * Minute));
    }

    [Fact]
    public void Clip_MirrorOverlappingTheLoop_IsClippedWhereTheLoopRuns()
    {
        // The loop runs from 30 to 50 min; the mirror writes 30-min temps every 10 min from 0 to 60 min.
        var loop = Stream("openaps://Trio", Day1Mills + 30 * Minute, count: 4, everyMinutes: 5, durationMinutes: 5, loop: true);
        var mirror = Stream("tidepool", Day1Mills, count: 7, everyMinutes: 10, durationMinutes: 30);

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, .. loop], Now);

        Spans(clipped.Where(tb => tb.Device == "tidepool")).Should().Equal(
            (Day1Mills, Day1Mills + 10 * Minute),
            (Day1Mills + 10 * Minute, Day1Mills + 20 * Minute),
            (Day1Mills + 20 * Minute, Day1Mills + 30 * Minute),
            (Day1Mills + 50 * Minute, Day1Mills + 60 * Minute),
            (Day1Mills + 60 * Minute, Day1Mills + 90 * Minute));
        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Should().Equal(
            (Day1Mills + 30 * Minute, Day1Mills + 35 * Minute),
            (Day1Mills + 35 * Minute, Day1Mills + 40 * Minute),
            (Day1Mills + 40 * Minute, Day1Mills + 45 * Minute),
            (Day1Mills + 45 * Minute, Day1Mills + 50 * Minute));
    }

    [Fact]
    public void Clip_PumpSwitch_TheOldStreamsRunningLastRecordEndsAtTheNewStreamsFirstRecord()
    {
        var old = Stream("openaps://Trio", Day1Mills, count: 2, everyMinutes: 5, durationMinutes: 30, loop: true);
        var oldRunning = RunningTemp("openaps://Trio", Day1Mills + 10 * Minute, loop: true);
        var replacement = new[]
        {
            Temp("medtronic", Day1Mills + 30 * Minute, 30),
            Temp("medtronic", Day1Mills + 60 * Minute, 30),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. old, oldRunning, .. replacement], Now);

        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Should().Equal(
            (Day1Mills, Day1Mills + 5 * Minute),
            (Day1Mills + 5 * Minute, Day1Mills + 10 * Minute),
            (Day1Mills + 10 * Minute, Day1Mills + 30 * Minute));
        Spans(clipped.Where(tb => tb.Device == "medtronic")).Should().Equal(
            (Day1Mills + 30 * Minute, Day1Mills + 60 * Minute),
            (Day1Mills + 60 * Minute, Day1Mills + 90 * Minute));
    }

    [Fact]
    public void Clip_MirrorWithARunningLastRecord_StillLosesToTheLoopsClosedRecords()
    {
        var loop = Stream("openaps://Trio", Day1Mills, count: 3, everyMinutes: 10, durationMinutes: 10, loop: true);
        var mirrorRunning = RunningTemp("tidepool", Day1Mills + 15 * Minute);
        var mirror = new[] { Temp("tidepool", Day1Mills + 5 * Minute, 30), mirrorRunning };

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, .. loop], Now);

        clipped.Should().NotContain(tb => tb.Device == "tidepool");
        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 10 * Minute),
            (Day1Mills + 10 * Minute, Day1Mills + 20 * Minute),
            (Day1Mills + 20 * Minute, Day1Mills + 30 * Minute));
    }

    [Fact]
    public void Clip_MirrorCopyStampedJustAfterTheLoopsRunningTemp_DoesNotEndIt()
    {
        var loop = new[]
        {
            Temp("openaps://Trio", Day1Mills - 5 * Minute, 5, loop: true),
            RunningTemp("openaps://Trio", Day1Mills, loop: true),
        };
        var mirror = new[]
        {
            Temp("tidepool", Day1Mills - 5 * Minute, 10),
            Temp("tidepool", Day1Mills + Minute, 10),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, .. loop], Now);

        clipped.Should().OnlyContain(tb => tb.Device == "openaps://Trio");
        Spans(clipped).Should().Equal((Day1Mills - 5 * Minute, Day1Mills), (Day1Mills, Now));
    }

    [Fact]
    public void Clip_MirrorRunningCopyStampedJustAfterTheLoopsRunningTemp_KeepsTheLoopsRate()
    {
        var loop = new[]
        {
            Temp("openaps://Trio", Day1Mills - 5 * Minute, 5, loop: true),
            RunningTemp("openaps://Trio", Day1Mills, loop: true),
        };
        var mirrorRunning = RunningTemp("tidepool", Day1Mills + Minute);
        mirrorRunning.Rate = 0.5;
        var mirror = new[] { Temp("tidepool", Day1Mills - 5 * Minute, 10), mirrorRunning };

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, .. loop], Now);

        clipped.Should().OnlyContain(tb => tb.Device == "openaps://Trio" && tb.Rate == 1.5);
        Spans(clipped).Should().Equal((Day1Mills - 5 * Minute, Day1Mills), (Day1Mills, Now));
    }

    [Fact]
    public void Clip_MirrorWithAnEarlierRecordAndOnePastTheSkewTolerance_EndsTheRunningTempThere()
    {
        var running = RunningTemp("openaps://Trio", Day1Mills, loop: true);
        var mirror = new[]
        {
            Temp("tidepool", Day1Mills - 30 * Minute, 10),
            Temp("tidepool", Day1Mills + 20 * Minute, 10),
        };

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, running], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills - 30 * Minute, Day1Mills - 20 * Minute),
            (Day1Mills, Day1Mills + 20 * Minute),
            (Day1Mills + 20 * Minute, Day1Mills + 30 * Minute));
        clipped.Select(tb => tb.Device).Should().Equal("tidepool", "openaps://Trio", "tidepool");
    }

    [Fact]
    public void Clip_RunningRecordOlderThanTheLongestTemp_EndsADayAfterItStarts()
    {
        var running = Running(Day1Mills);

        var clipped = StatisticsService.ClipOverlappingTempBasals([running], Day1Mills + 30 * 60 * Minute);

        Spans(clipped).Should().Equal((Day1Mills, Day1Mills + 24 * 60 * Minute));
    }

    [Fact]
    public void Clip_MirrorAheadWithARunningLastRecord_LeavesTheLoopsRunningTempToNow()
    {
        var loop = Stream("openaps://Trio", At(0), count: 12, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = WithRate(0.5, [
            .. Stream("tidepool", At(-1), count: 12, everyMinutes: 5, durationMinutes: 30),
            RunningTemp("tidepool", At(59))]);

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. mirror], Now);

        Spans(clipped.Where(tb => tb.Device == "tidepool")).Should().Equal((At(-1), At(0)));
        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(60), Now));
    }

    [Fact]
    public void Clip_MirrorBehindWithARunningLastRecord_LeavesTheLoopsRunningTempToNow()
    {
        var loop = Stream("openaps://Trio", At(0), count: 12, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = WithRate(0.5, [
            .. Stream("tidepool", At(1), count: 12, everyMinutes: 5, durationMinutes: 30),
            RunningTemp("tidepool", At(61))]);

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. mirror], Now);

        clipped.Should().OnlyContain(tb => tb.Device == "openaps://Trio");
        Spans(clipped).Last().Should().Be((At(60), Now));
    }

    [Fact]
    public void Clip_MirrorBehindRunningPastTheLoopsLastRecord_LeavesTheLoopsRunningTempToNow()
    {
        var loop = Stream("openaps://Trio", At(0), count: 12, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = WithRate(0.5, Stream("tidepool", At(1), count: 13, everyMinutes: 5, durationMinutes: 30));

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. mirror], Now);

        clipped.Should().OnlyContain(tb => tb.Device == "openaps://Trio");
        Spans(clipped).Last().Should().Be((At(60), Now));
    }

    [Fact]
    public void Clip_MirrorAheadAndClosed_LeavesTheLoopsRunningTempToNow()
    {
        var loop = Stream("openaps://Trio", At(0), count: 12, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = WithRate(0.5, Stream("tidepool", At(-1), count: 13, everyMinutes: 5, durationMinutes: 30));

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. mirror], Now);

        Spans(clipped.Where(tb => tb.Device == "tidepool")).Should().Equal((At(-1), At(0)));
        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(60), Now));
    }

    [Fact]
    public void Clip_MirrorFillingAGapInTheLoop_DeliversOnlyInTheGap()
    {
        TempBasal[] loop =
        [
            .. Stream("openaps://Trio", At(0), count: 12, everyMinutes: 5, durationMinutes: 5, loop: true),
            .. Stream("openaps://Trio", At(120), count: 6, everyMinutes: 5, durationMinutes: 5, loop: true),
            RunningTemp("openaps://Trio", At(150), loop: true),
        ];
        var mirror = WithRate(0.5, [
            .. Stream("tidepool", At(1), count: 30, everyMinutes: 5, durationMinutes: 5),
            RunningTemp("tidepool", At(151))]);

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. loop, .. mirror], Now);

        var fromMirror = Spans(clipped.Where(tb => tb.Device == "tidepool")).ToList();
        fromMirror.First().Item1.Should().Be(At(60));
        fromMirror.Last().Item2.Should().Be(At(120));
        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(150), Now));
    }

    [Fact]
    public void Clip_SwitchBackToALowerRankedPumpWithEarlierHistory_EndsTheLoopsRunningTempAtItsRecords()
    {
        var oldPump = WithRate(0.8, Stream("medtronic", At(-300), count: 6, everyMinutes: 30, durationMinutes: 30));
        var loop = Stream("openaps://Trio", At(-120), count: 36, everyMinutes: 5, durationMinutes: 30, loop: true);
        var newPump = WithRate(0.8, [
            .. Stream("medtronic", At(90), count: 3, everyMinutes: 30, durationMinutes: 30),
            RunningTemp("medtronic", At(180))]);

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. oldPump, .. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. newPump], Now);

        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(60), At(90)));
        Spans(clipped.Where(tb => tb.Device == "medtronic" && tb.StartMills >= At(0))).Should().Equal(
            (At(90), At(120)), (At(120), At(150)), (At(150), At(180)), (At(180), Now));
    }

    [Fact]
    public void Clip_SwitchBackToALowerRankedPumpWithOneEarlierRecord_EndsTheLoopsRunningTempAtItsRecords()
    {
        var loop = Stream("openaps://Trio", At(-120), count: 36, everyMinutes: 5, durationMinutes: 30, loop: true);
        var newPump = WithRate(0.8, Stream("medtronic", At(90), count: 21, everyMinutes: 30, durationMinutes: 30));

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [Temp("medtronic", At(-300), 30), .. loop, RunningTemp("openaps://Trio", At(60), loop: true), .. newPump],
            Now);

        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(60), At(90)));
        var fromNewPump = Spans(clipped.Where(tb => tb.Device == "medtronic" && tb.StartMills >= At(0))).ToList();
        fromNewPump.First().Item1.Should().Be(At(90));
        fromNewPump.Last().Item2.Should().Be(Now);
        fromNewPump.Sum(span => span.Item2 - span.Item1).Should().Be(Now - At(90));
    }

    [Fact]
    public void Clip_LoopStopsWhileTheMirrorRecordsOn_TheMirrorTakesOverPastTheSkewTolerance()
    {
        var loop = Stream("openaps://Trio", At(0), count: 11, everyMinutes: 5, durationMinutes: 5, loop: true);
        var mirror = WithRate(0.5, Stream("tidepool", At(1), count: 60, everyMinutes: 5, durationMinutes: 5));

        var clipped = StatisticsService.ClipOverlappingTempBasals(
            [.. loop, RunningTemp("openaps://Trio", At(55), loop: true), .. mirror], Now);

        Spans(clipped.Where(tb => tb.Device == "openaps://Trio")).Last().Should().Be((At(55), At(66)));
        var fromMirror = Spans(clipped.Where(tb => tb.Device == "tidepool")).ToList();
        fromMirror.First().Should().Be((At(66), At(71)));
        fromMirror.Last().Should().Be((At(296), At(301)));
        clipped.Should().NotContain(tb => tb.EndMills > At(301));
    }

    [Fact]
    public void Clip_MirrorWhoseEarlierRecordsAreNotInTheInput_ReadsAsASuccessor()
    {
        var running = RunningTemp("openaps://Trio", Day1Mills, loop: true);
        var mirrorInWindow = Temp("tidepool", Day1Mills + 20 * Minute, 10);

        var clipped = StatisticsService.ClipOverlappingTempBasals([mirrorInWindow, running], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 20 * Minute),
            (Day1Mills + 20 * Minute, Day1Mills + 30 * Minute));
    }

    [Fact]
    public void Clip_MirrorRecordSpanningAGapInTheLoop_KeepsThePiecesTheLoopLeavesOpen()
    {
        var loop = new[]
        {
            Temp("openaps://Trio", Day1Mills, 10, loop: true),
            Temp("openaps://Trio", Day1Mills + 20 * Minute, 10, loop: true),
        };
        var mirror = Temp("tidepool", Day1Mills - 5 * Minute, 40);

        var clipped = StatisticsService.ClipOverlappingTempBasals([mirror, .. loop], Now);

        Spans(clipped.Where(tb => tb.Device == "tidepool")).Should().Equal(
            (Day1Mills - 5 * Minute, Day1Mills),
            (Day1Mills + 10 * Minute, Day1Mills + 20 * Minute),
            (Day1Mills + 30 * Minute, Day1Mills + 35 * Minute));
    }

    [Fact]
    public void Clip_LoopStreamKnownOnlyByItsSnapshotLink_Wins()
    {
        var loop = Temp("openaps://Trio", Day1Mills, 30);
        loop.ApsSnapshotId = Guid.NewGuid();
        var mirror = Stream("tidepool", Day1Mills, count: 3, everyMinutes: 10, durationMinutes: 10);

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, loop], Now);

        clipped.Should().ContainSingle().Which.Device.Should().Be("openaps://Trio");
    }

    [Fact]
    public void Clip_NoSnapshotEvidence_TheAidUploaderWinsWithFewerRecords()
    {
        var loop = Temp("openaps://Trio", Day1Mills, 30);
        var mirror = Stream("tidepool", Day1Mills, count: 3, everyMinutes: 10, durationMinutes: 10);

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, loop], Now);

        clipped.Should().ContainSingle().Which.Device.Should().Be("openaps://Trio");
    }

    [Fact]
    public void Clip_NoSnapshotEvidence_TwoOverlappingWindowsPickTheSameStream()
    {
        // The mirror outnumbers the loop in the first window and not in the second.
        var loop = Stream("openaps://Trio", Day1Mills, count: 12, everyMinutes: 10, durationMinutes: 30);
        var mirror = Stream("tidepool", Day1Mills, count: 12, everyMinutes: 5, durationMinutes: 5);
        TempBasal[] all = [.. loop, .. mirror];
        bool StartsIn(TempBasal tb, long from, long to) => tb.StartMills >= from && tb.StartMills < to;

        var first = StatisticsService.ClipOverlappingTempBasals(
            all.Where(tb => StartsIn(tb, Day1Mills, Day1Mills + 60 * Minute)), Now);
        var second = StatisticsService.ClipOverlappingTempBasals(
            all.Where(tb => StartsIn(tb, Day1Mills + 30 * Minute, Day1Mills + 120 * Minute)), Now);

        bool InOverlap(TempBasal tb) => tb.StartMills < Day1Mills + 60 * Minute && tb.EndMills > Day1Mills + 30 * Minute;
        first.Where(InOverlap).Select(tb => tb.Device).Should().OnlyContain(d => d == "openaps://Trio");
        second.Where(InOverlap).Select(tb => tb.Device).Should().OnlyContain(d => d == "openaps://Trio");
    }

    [Fact]
    public void Clip_NoLoopStream_TheStreamWithMoreRecordsWins()
    {
        var coarse = Temp("pump-a", Day1Mills, 30);
        var fine = Stream("pump-b", Day1Mills + 10 * Minute, count: 2, everyMinutes: 5, durationMinutes: 5);

        var clipped = StatisticsService.ClipOverlappingTempBasals([coarse, .. fine], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 10 * Minute),
            (Day1Mills + 10 * Minute, Day1Mills + 15 * Minute),
            (Day1Mills + 15 * Minute, Day1Mills + 20 * Minute),
            (Day1Mills + 20 * Minute, Day1Mills + 30 * Minute));
        clipped.Select(tb => tb.Device).Should().Equal("pump-a", "pump-b", "pump-b", "pump-a");
    }

    [Fact]
    public void Clip_NoLoopStreamAndEqualCounts_TheEarlierStreamWins()
    {
        var earlier = Temp("pump-b", Day1Mills, 30);
        var later = Temp("pump-a", Day1Mills + 10 * Minute, 30);

        var clipped = StatisticsService.ClipOverlappingTempBasals([later, earlier], Now);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 30 * Minute),
            (Day1Mills + 30 * Minute, Day1Mills + 40 * Minute));
        clipped.Select(tb => tb.Device).Should().Equal("pump-b", "pump-a");
    }

    [Fact]
    public void Clip_MirrorJudgedByAnotherDevicesSnapshot_LosesToTheLoopStatingItsOrigin()
    {
        var loop = WithRate(0, Stream("loop://iPhone", Day1Mills, count: 6, everyMinutes: 5, durationMinutes: 30));
        foreach (var tb in loop)
            tb.OriginEvidence = TempBasalOriginEvidence.Reported;
        var mirror = WithRate(3.0, [Temp("tidepool", Day1Mills, 30)]);
        mirror[0].OriginEvidence = TempBasalOriginEvidence.OtherDevice;
        mirror[0].ApsSnapshotId = Guid.NewGuid();

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, .. loop], Now);

        clipped.Should().OnlyContain(tb => tb.Device == "loop://iPhone");
        clipped.Sum(tb => StatisticsService.GetTempBasalInsulin(tb)!.Value).Should().Be(0, "the loop's own records say it zero-temped");
    }

    [Fact]
    public void Clip_StreamJudgedOnlyByAnotherDevice_RanksBelowAStreamStatingItsOrigin()
    {
        var stated = Temp("pump-a", Day1Mills, 30);
        stated.OriginEvidence = TempBasalOriginEvidence.Reported;
        var mirror = Stream("pump-b", Day1Mills, count: 3, everyMinutes: 10, durationMinutes: 10);
        foreach (var tb in mirror)
            tb.OriginEvidence = TempBasalOriginEvidence.OtherDevice;

        var clipped = StatisticsService.ClipOverlappingTempBasals([.. mirror, stated], Now);

        clipped.Should().ContainSingle().Which.Device.Should().Be("pump-a", "the mirror's three records would otherwise outnumber it");
    }

    [Fact]
    public void Clip_RunningRecordBeforeAnUploadGap_StopsAfterTheLongestTempAPumpAccepts()
    {
        var running = Running(Day1Mills);
        var next = MakeTempBasal(rate: 0.5, durationMinutes: 30, mills: Day1Mills + 3 * 24 * 60 * Minute);

        var clipped = StatisticsService.ClipOverlappingTempBasals([running, next], next.EndMills!.Value);

        Spans(clipped).Should().Equal(
            (Day1Mills, Day1Mills + 24 * 60 * Minute),
            (next.StartMills, next.EndMills!.Value));
    }

    [Fact]
    public void Clip_RunningRecordBeforeItsStreamsNextRecord_IsEndedByAnotherStreamRecordingOn()
    {
        var running = RunningTemp("pump-a", At(0));
        TempBasal[] after = [Temp("pump-a", At(180), 30), Temp("pump-a", At(210), 30)];
        TempBasal[] other = [Temp("pump-b", At(-30), 20), Temp("pump-b", At(30), 5)];

        var clipped = StatisticsService.ClipOverlappingTempBasals([running, .. after, .. other], Now);

        Spans(clipped.Where(tb => tb.Device == "pump-a")).Should().Equal(
            (At(0), At(30)), (At(180), At(210)), (At(210), At(240)));
    }

    [Fact]
    public void InsulinDelivery_MirrorOverlappingTheLoop_CountsTheLoopOnce()
    {
        var loop = Stream("openaps://Trio", Day1Mills, count: 12, everyMinutes: 5, durationMinutes: 30, loop: true);
        var mirror = Stream("tidepool", Day1Mills + 2 * Minute, count: 12, everyMinutes: 5, durationMinutes: 5);
        foreach (var tb in mirror)
            tb.Rate = 3.0;

        var loopOnly = _sut.CalculateInsulinDeliveryStatistics([], [], loop, [], StartDate, EndDate, Mills(EndDate));
        var both = _sut.CalculateInsulinDeliveryStatistics([], [], [.. loop, .. mirror], [], StartDate, EndDate, Mills(EndDate));

        both.TotalBasal.Should().BeApproximately(loopOnly.TotalBasal, 1e-9);
    }

    [Fact]
    public void InsulinDelivery_RunningTemp_CountsToTheWindowEnd()
    {
        var running = Running(Mills(EndDate) - 120 * Minute);

        var delivery = _sut.CalculateInsulinDeliveryStatistics([], [], [running], [], StartDate, EndDate, Mills(EndDate));

        delivery.TotalBasal.Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void InsulinDelivery_WindowEndingAfterNow_StopsARunningTempAtNow()
    {
        var now = Mills(EndDate) - 90 * Minute;
        var running = Running(Mills(EndDate) - 120 * Minute);

        var delivery = _sut.CalculateInsulinDeliveryStatistics([], [], [running], [], StartDate, EndDate, now);

        delivery.TotalBasal.Should().BeApproximately(0.5, 1e-9);
        delivery.WindowDays.Should().Be(7);
    }

    private static IEnumerable<(long, long)> Spans(IEnumerable<TempBasal> clipped) =>
        clipped.Select(tb => (tb.StartMills, tb.EndMills!.Value));

    private static TempBasal Running(long mills)
    {
        var tb = MakeTempBasal(rate: 1.0, durationMinutes: 0, mills);
        tb.EndTimestamp = null;
        return tb;
    }

    private static TempBasal RunningTemp(string device, long mills, bool loop = false)
    {
        var tb = Temp(device, mills, 0, loop);
        tb.EndTimestamp = null;
        return tb;
    }

    private static long At(int minutes) => Day1Mills + minutes * Minute;

    private static TempBasal[] WithRate(double rate, TempBasal[] temps)
    {
        foreach (var tb in temps)
            tb.Rate = rate;
        return temps;
    }

    private static TempBasal Cancel(long mills) => MakeTempBasal(rate: 0, durationMinutes: 0, mills);

    private static TempBasal Temp(string device, long mills, double durationMinutes, bool loop = false)
    {
        var tb = MakeTempBasal(rate: 1.5, durationMinutes, mills);
        tb.Device = device;
        if (loop)
            tb.OriginEvidence = TempBasalOriginEvidence.Snapshot;
        return tb;
    }

    private static TempBasal[] Stream(
        string device, long from, int count, int everyMinutes, double durationMinutes, bool loop = false) =>
        [.. Enumerable.Range(0, count).Select(i => Temp(device, from + i * everyMinutes * Minute, durationMinutes, loop))];

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

    private static CarbIntake MakeCarbIntake(double carbs, long mills)
    {
        return new CarbIntake
        {
            Carbs = carbs,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime,
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
