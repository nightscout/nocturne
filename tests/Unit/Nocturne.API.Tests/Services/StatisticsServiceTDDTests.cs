using FluentAssertions;
using Nocturne.API.Services;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Tests for Total Daily Dose (TDD) and insulin delivery calculations.
/// Ensures consistency across all calculation paths:
///   - CalculateTreatmentSummary
///   - CalculateInsulinDeliveryStatistics
///   - CalculateDailyBasalBolusRatios
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

    #region IsBolusTreatment Classification

    [Theory]
    [InlineData("Meal Bolus", true)]
    [InlineData("Correction Bolus", true)]
    [InlineData("Snack Bolus", true)]
    [InlineData("Bolus Wizard", true)]
    [InlineData("Combo Bolus", true)]
    [InlineData("Bolus", true)]
    [InlineData("SMB", true)]
    [InlineData("e-Bolus", true)]
    [InlineData("Extended Bolus", true)]
    [InlineData("Dual Wave", true)]
    [InlineData("Temp Basal", false)]
    [InlineData("TempBasal", false)]
    [InlineData("Temporary Basal", false)]
    [InlineData("Profile Switch", false)]
    [InlineData("Site Change", false)]
    [InlineData("BG Check", false)]
    [InlineData("Note", false)]
    [InlineData("", false)]
    public void IsBolusTreatment_ShouldClassifyCorrectly(string eventType, bool expectedIsBolus)
    {
        var treatment = new Treatment { EventType = eventType };

        var result = _sut.IsBolusTreatment(treatment);

        result.Should().Be(expectedIsBolus,
            $"EventType '{eventType}' should {(expectedIsBolus ? "" : "NOT ")}be classified as bolus");
    }

    [Fact]
    public void IsBolusTreatment_NullEventType_ShouldReturnFalse()
    {
        var treatment = new Treatment { EventType = null };

        _sut.IsBolusTreatment(treatment).Should().BeFalse(
            "null EventType should default to non-bolus (basal) classification");
    }

    [Theory]
    [InlineData("meal bolus")]
    [InlineData("MEAL BOLUS")]
    [InlineData("Meal bolus")]
    [InlineData("smb")]
    public void IsBolusTreatment_ShouldBeCaseInsensitive(string eventType)
    {
        var treatment = new Treatment { EventType = eventType };

        _sut.IsBolusTreatment(treatment).Should().BeTrue(
            $"'{eventType}' should match case-insensitively");
    }

    #endregion

    #region Treatment.Insulin Auto-Calculation

    [Fact]
    public void Treatment_Insulin_WithExplicitValue_ShouldReturnExplicitValue()
    {
        var treatment = new Treatment { Insulin = 5.0 };

        treatment.Insulin.Should().Be(5.0);
    }

    [Fact]
    public void Treatment_Insulin_WithRateAndDuration_ShouldAutoCalculate()
    {
        // Rate = 1.0 U/hr, Duration = 30 min → Insulin = 1.0 * (30/60) = 0.5U
        var treatment = new Treatment { Rate = 1.0, Duration = 30 };

        treatment.Insulin.Should().Be(0.5);
    }

    [Fact]
    public void Treatment_Insulin_WithRateAndDuration_60Minutes_ShouldEqualRate()
    {
        // Rate = 0.8 U/hr, Duration = 60 min → Insulin = 0.8 * (60/60) = 0.8U
        var treatment = new Treatment { Rate = 0.8, Duration = 60 };

        treatment.Insulin.Should().Be(0.8);
    }

    [Fact]
    public void Treatment_Insulin_WithZeroRate_ShouldReturnZero()
    {
        // Suspension: Rate = 0 U/hr, Duration = 30 min → Insulin = 0
        var treatment = new Treatment { Rate = 0, Duration = 30 };

        treatment.Insulin.Should().Be(0);
    }

    [Fact]
    public void Treatment_Insulin_NoInsulinNoRateNoDuration_ShouldReturnNull()
    {
        var treatment = new Treatment { EventType = "Note" };

        treatment.Insulin.Should().BeNull();
    }

    [Fact]
    public void Treatment_Insulin_ExplicitValueOverridesRateAndDuration()
    {
        // Even though Rate * Duration would give a different number,
        // explicit Insulin takes precedence
        var treatment = new Treatment
        {
            Insulin = 3.0,
            Rate = 1.0,
            Duration = 60,
        };

        treatment.Insulin.Should().Be(3.0,
            "explicit Insulin value should take precedence over Rate * Duration calculation");
    }

    #endregion

    #region CalculateTreatmentSummary — Basic Scenarios

    [Fact]
    public void TreatmentSummary_BolusOnly_ShouldSumCorrectly()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 4.0),
            MakeBolus("Correction Bolus", 2.5),
            MakeBolus("SMB", 0.3),
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Insulin.Bolus.Should().Be(6.8);
        result.Totals.Insulin.Basal.Should().Be(0);
    }

    [Fact]
    public void TreatmentSummary_BasalFromRateAndDuration_ShouldCalculateCorrectly()
    {
        var treatments = new[]
        {
            // 1.0 U/hr for 30 min = 0.5U
            MakeTempBasal(rate: 1.0, durationMinutes: 30),
            // 0.8 U/hr for 60 min = 0.8U
            MakeTempBasal(rate: 0.8, durationMinutes: 60),
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Insulin.Basal.Should().Be(1.3);
        result.Totals.Insulin.Bolus.Should().Be(0);
    }

    [Fact]
    public void TreatmentSummary_MixedBolusAndBasal_ShouldSplitCorrectly()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0),
            MakeBolus("Correction Bolus", 1.5),
            MakeTempBasal(rate: 1.2, durationMinutes: 60), // 1.2U
            MakeTempBasal(rate: 0.6, durationMinutes: 30), // 0.3U
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Insulin.Bolus.Should().Be(6.5);
        result.Totals.Insulin.Basal.Should().Be(1.5);
        _sut.GetTotalInsulin(result).Should().Be(8.0);
    }

    [Fact]
    public void TreatmentSummary_Suspension_ShouldNotAddInsulin()
    {
        // Suspension = Rate 0 for a duration → 0 insulin
        var treatments = new[]
        {
            MakeTempBasal(rate: 0.0, durationMinutes: 30),
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Insulin.Basal.Should().Be(0);
    }

    [Fact]
    public void TreatmentSummary_CarbsAndMacros_ShouldAggregate()
    {
        var treatments = new[]
        {
            new Treatment { EventType = "Meal Bolus", Insulin = 4.0, Carbs = 45, Protein = 20, Fat = 10 },
            new Treatment { EventType = "Snack Bolus", Insulin = 1.0, Carbs = 15 },
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Food.Carbs.Should().Be(60);
        result.Totals.Food.Protein.Should().Be(20);
        result.Totals.Food.Fat.Should().Be(10);
        result.CarbToInsulinRatio.Should().Be(12, "60g / 5U = 12");
    }

    #endregion

    #region CalculateInsulinDeliveryStatistics — Basic Scenarios

    [Fact]
    public void InsulinDelivery_BolusOnly_ShouldCalculateCorrectly()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 4.0, Day1Mills),
            MakeBolus("Correction Bolus", 2.5, Day1Mills),
            MakeBolus("SMB", 0.3, Day1Mills),
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        result.TotalBolus.Should().Be(6.8);
        result.TotalBasal.Should().Be(0);
        result.TotalInsulin.Should().Be(6.8);
        result.BolusCount.Should().Be(3);
    }

    [Fact]
    public void InsulinDelivery_BasalFromRateAndDuration_ShouldCalculateCorrectly()
    {
        var treatments = new[]
        {
            MakeTempBasal(rate: 1.0, durationMinutes: 30, mills: Day1Mills), // 0.5U
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day1Mills), // 0.8U
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        result.TotalBasal.Should().Be(1.3);
        result.TotalBolus.Should().Be(0);
        result.TotalInsulin.Should().Be(1.3);
    }

    [Fact]
    public void InsulinDelivery_TDD_ShouldDivideByDateRangeSpan()
    {
        // 7-day range, 70U total → TDD should be 10U/day
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 35.0, Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60 * 35, mills: Day1Mills), // 35U
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        result.Tdd.Should().Be(10.0, "70U / 7 days = 10 U/day");
        result.DayCount.Should().Be(7);
    }

    #endregion

    #region Consistency: TreatmentSummary vs InsulinDeliveryStatistics

    [Fact]
    public void Consistency_SameTreatments_TotalInsulinShouldMatch()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0, Day1Mills),
            MakeBolus("Correction Bolus", 2.0, Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeTempBasal(rate: 0.5, durationMinutes: 120, mills: Day2Mills), // 1.0U
            MakeBolus("SMB", 0.5, Day2Mills),
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        var summaryTotal = _sut.GetTotalInsulin(summary);

        summaryTotal.Should().Be(delivery.TotalInsulin,
            "TreatmentSummary and InsulinDeliveryStatistics should agree on total insulin");
    }

    [Fact]
    public void Consistency_SameTreatments_BolusAndBasalShouldMatch()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0, Day1Mills),
            MakeBolus("Correction Bolus", 2.0, Day2Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills),
            MakeTempBasal(rate: 0.5, durationMinutes: 120, mills: Day2Mills),
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        summary.Totals.Insulin.Bolus.Should().Be(delivery.TotalBolus,
            "bolus totals should match between methods");
        summary.Totals.Insulin.Basal.Should().Be(delivery.TotalBasal,
            "basal totals should match between methods");
    }

    [Fact]
    public void Consistency_DailyRatios_TotalShouldMatchOtherMethods()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0, Day1Mills),
            MakeBolus("Correction Bolus", 2.0, Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U
            MakeBolus("Meal Bolus", 4.0, Day2Mills),
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day2Mills), // 0.8U
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(treatments);

        var summaryTotal = _sut.GetTotalInsulin(summary);
        var ratiosGrandTotal = ratios.DailyData.Sum(d => d.Total);

        summaryTotal.Should().Be(delivery.TotalInsulin,
            "summary total should match delivery total");
        ratiosGrandTotal.Should().Be(delivery.TotalInsulin,
            "daily ratios grand total should match delivery total");
    }

    [Fact]
    public void Consistency_PercentagesShouldSumTo100()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0, Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills),
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        var summaryBolusPercent = _sut.GetBolusPercentage(summary);
        var summaryBasalPercent = _sut.GetBasalPercentage(summary);

        (summaryBolusPercent + summaryBasalPercent).Should().BeApproximately(100.0, 0.1,
            "TreatmentSummary basal% + bolus% should sum to 100");
        (delivery.BolusPercent + delivery.BasalPercent).Should().BeApproximately(100.0, 0.1,
            "InsulinDelivery basal% + bolus% should sum to 100");
    }

    #endregion

    #region Realistic AID System Patterns

    [Fact]
    public void RealisticPattern_AndroidAPS_SMBsAndTempBasals_ShouldSumCorrectly()
    {
        // AndroidAPS pattern: many SMBs (small boluses) + frequent temp basals
        var treatments = new List<Treatment>();
        var baseMills = Day1Mills;

        // 24 temp basals over the day (one per hour), varying rates
        for (int hour = 0; hour < 24; hour++)
        {
            var mills = baseMills + (hour * 3600000L);
            treatments.Add(MakeTempBasal(
                rate: 0.5 + (hour % 4) * 0.2, // Varies 0.5-1.1 U/hr
                durationMinutes: 60,
                mills: mills
            ));
        }

        // 8 SMBs throughout the day (typical for loop system)
        for (int i = 0; i < 8; i++)
        {
            var mills = baseMills + ((i * 3 + 1) * 3600000L); // Every 3 hours
            treatments.Add(MakeBolus("SMB", 0.3 + (i % 3) * 0.2, mills));
        }

        // 3 meal boluses
        treatments.Add(MakeBolus("Meal Bolus", 4.5, baseMills + 7 * 3600000L));  // breakfast
        treatments.Add(MakeBolus("Meal Bolus", 6.0, baseMills + 12 * 3600000L)); // lunch
        treatments.Add(MakeBolus("Meal Bolus", 5.5, baseMills + 18 * 3600000L)); // dinner

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        // All three methods should agree on totals
        _sut.GetTotalInsulin(summary).Should().Be(delivery.TotalInsulin,
            "AID pattern: summary and delivery totals should match");

        // Bolus should be meal boluses + SMBs
        delivery.TotalBolus.Should().BeGreaterThan(0);
        delivery.TotalBasal.Should().BeGreaterThan(0);
        delivery.BolusCount.Should().Be(11, "3 meals + 8 SMBs = 11 boluses");
    }

    [Fact]
    public void RealisticPattern_Omnipod_BolusOnlyTreatments_ShouldReportBolusCorrectly()
    {
        // Omnipod pattern: only bolus treatments uploaded (basal is not in treatments)
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 4.0, Day1Mills),
            MakeBolus("Correction Bolus", 1.5, Day1Mills + 3600000),
            MakeBolus("Meal Bolus", 5.5, Day1Mills + 7 * 3600000L),
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        // Only bolus insulin should be reported
        summary.Totals.Insulin.Bolus.Should().Be(11.0);
        summary.Totals.Insulin.Basal.Should().Be(0,
            "no basal treatments uploaded — basal should be 0");
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
        var treatments = new[]
        {
            // Long-acting basal (e.g., Lantus 20U once daily)
            new Treatment
            {
                EventType = "Basal",
                Insulin = 20.0,
                Mills = Day1Mills,
            },
            MakeBolus("Meal Bolus", 5.0, Day1Mills + 7 * 3600000L),
            MakeBolus("Meal Bolus", 7.0, Day1Mills + 12 * 3600000L),
            MakeBolus("Meal Bolus", 6.0, Day1Mills + 18 * 3600000L),
            MakeBolus("Correction Bolus", 2.0, Day1Mills + 21 * 3600000L),
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        summary.Totals.Insulin.Basal.Should().Be(20.0, "long-acting basal = 20U");
        summary.Totals.Insulin.Bolus.Should().Be(20.0, "meal + correction boluses = 20U");
        _sut.GetTotalInsulin(summary).Should().Be(40.0);

        delivery.TotalBasal.Should().Be(20.0);
        delivery.TotalBolus.Should().Be(20.0);
        delivery.TotalInsulin.Should().Be(40.0);

        // 50/50 split
        _sut.GetBolusPercentage(summary).Should().Be(50.0);
        _sut.GetBasalPercentage(summary).Should().Be(50.0);
    }

    #endregion

    #region DailyBasalBolusRatios

    [Fact]
    public void DailyRatios_MultiDayData_ShouldGroupByDay()
    {
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 5.0, Day1Mills),
            MakeTempBasal(rate: 1.0, durationMinutes: 60, mills: Day1Mills), // 1.0U

            MakeBolus("Meal Bolus", 3.0, Day2Mills),
            MakeTempBasal(rate: 0.8, durationMinutes: 60, mills: Day2Mills), // 0.8U
        };

        var result = _sut.CalculateDailyBasalBolusRatios(treatments);

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
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 10.0, Day1Mills),
            MakeBolus("Meal Bolus", 20.0, Day2Mills),
        };

        var result = _sut.CalculateDailyBasalBolusRatios(treatments);

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
        // Treatments on only 2 out of 7 days
        var treatments = new[]
        {
            MakeBolus("Meal Bolus", 35.0, Day1Mills),
            MakeBolus("Meal Bolus", 35.0, Day2Mills),
        };

        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(treatments);

        // InsulinDelivery: 70U / 7 days = 10 U/day
        delivery.DayCount.Should().Be(7);
        delivery.Tdd.Should().Be(10.0);

        // DailyRatios: 70U / 2 days = 35 U/day
        ratios.DayCount.Should().Be(2);
        ratios.AverageTdd.Should().Be(35.0);

        // These INTENTIONALLY differ — document this design choice:
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

        // Day 1: 25U, Day 2: 35U → Avg = 30 U/day
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
    public void EmptyTreatments_AllMethods_ShouldReturnZeroes()
    {
        var empty = Array.Empty<Treatment>();

        var summary = _sut.CalculateTreatmentSummary(empty);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(empty, StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(empty);

        _sut.GetTotalInsulin(summary).Should().Be(0);
        delivery.TotalInsulin.Should().Be(0);
        delivery.Tdd.Should().Be(0);
        ratios.AverageTdd.Should().Be(0);
    }

    [Fact]
    public void TreatmentsWithNoInsulin_ShouldNotCountAsInsulinDelivery()
    {
        // Treatments that have carbs but no insulin
        var treatments = new[]
        {
            new Treatment { EventType = "Meal Bolus", Carbs = 45, Mills = Day1Mills },
            new Treatment { EventType = "BG Check", Glucose = 120, Mills = Day1Mills },
        };

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        _sut.GetTotalInsulin(summary).Should().Be(0);
        delivery.TotalInsulin.Should().Be(0);
        summary.Totals.Food.Carbs.Should().Be(45);
    }

    [Fact]
    public void VerySmallInsulinAmounts_ShouldNotBeLost()
    {
        // SMBs can be very small (0.05U increments on some pumps)
        var treatments = Enumerable.Range(0, 20)
            .Select(i => MakeBolus("SMB", 0.05, Day1Mills + i * 300000L))
            .ToArray();

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);

        // 20 * 0.05 = 1.0U
        summary.Totals.Insulin.Bolus.Should().BeApproximately(1.0, 0.001,
            "many small SMBs should sum correctly without floating-point drift");
        delivery.TotalBolus.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void UnrecognizedEventType_WithInsulin_ShouldBeClassifiedAsBasal()
    {
        // Unknown event type defaults to basal classification
        var treatment = new Treatment
        {
            EventType = "CustomPumpEvent",
            Insulin = 5.0,
            Mills = Day1Mills,
        };

        var summary = _sut.CalculateTreatmentSummary(new[] { treatment });

        summary.Totals.Insulin.Basal.Should().Be(5.0,
            "unrecognized EventType should be classified as basal (not bolus)");
        summary.Totals.Insulin.Bolus.Should().Be(0);
    }

    [Fact]
    public void TreatmentWithAbsoluteNotRate_ShouldStillCalculateInsulin()
    {
        // Some systems use 'absolute' instead of 'rate'
        var treatment = new Treatment
        {
            EventType = "Temp Basal",
            Absolute = 1.5,
            Duration = 30,
            Mills = Day1Mills,
        };

        // Treatment.Insulin should auto-calculate: 1.5 * (30/60) = 0.75
        treatment.Insulin.Should().Be(0.75);

        var summary = _sut.CalculateTreatmentSummary(new[] { treatment });
        summary.Totals.Insulin.Basal.Should().Be(0.75);
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

    #endregion

    #region Multi-Day Realistic Scenario

    [Fact]
    public void SevenDayRealisticScenario_AllMethodsShouldBeConsistent()
    {
        var treatments = new List<Treatment>();
        var dailyExpectedBolus = new double[] { 18.0, 22.0, 15.0, 20.0, 19.0, 21.0, 17.0 };
        var dailyExpectedBasal = new double[] { 24.0, 24.0, 23.5, 24.0, 22.0, 24.5, 24.0 };

        for (int day = 0; day < 7; day++)
        {
            var dayMills = new DateTimeOffset(2024, 1, 1 + day, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

            // 3 meal boluses per day
            treatments.Add(MakeBolus("Meal Bolus", dailyExpectedBolus[day] * 0.4, dayMills));
            treatments.Add(MakeBolus("Meal Bolus", dailyExpectedBolus[day] * 0.35, dayMills + 4 * 3600000L));
            treatments.Add(MakeBolus("Meal Bolus", dailyExpectedBolus[day] * 0.25, dayMills + 10 * 3600000L));

            // 24 hourly temp basals per day
            for (int hour = 0; hour < 24; hour++)
            {
                var hourMills = new DateTimeOffset(2024, 1, 1 + day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                treatments.Add(MakeTempBasal(
                    rate: dailyExpectedBasal[day] / 24.0, // Even hourly rate
                    durationMinutes: 60,
                    mills: hourMills
                ));
            }
        }

        var summary = _sut.CalculateTreatmentSummary(treatments);
        var delivery = _sut.CalculateInsulinDeliveryStatistics(treatments, StartDate, EndDate);
        var ratios = _sut.CalculateDailyBasalBolusRatios(treatments);

        var expectedTotalBolus = dailyExpectedBolus.Sum();
        var expectedTotalBasal = dailyExpectedBasal.Sum();
        var expectedTotal = expectedTotalBolus + expectedTotalBasal;

        // All methods should agree on totals (within rounding tolerance)
        _sut.GetTotalInsulin(summary).Should().BeApproximately(expectedTotal, 0.1);
        delivery.TotalInsulin.Should().BeApproximately(expectedTotal, 0.1);

        var ratiosTotal = ratios.DailyData.Sum(d => d.Total);
        ratiosTotal.Should().BeApproximately(expectedTotal, 1.0); // Slightly more rounding from per-day rounding

        // TDD should be ~45U/day
        var expectedAvgTdd = expectedTotal / 7.0;
        delivery.Tdd.Should().BeApproximately(expectedAvgTdd, 0.5);
    }

    #endregion

    #region Helper Methods

    private static Treatment MakeBolus(string eventType, double insulin, long? mills = null)
    {
        return new Treatment
        {
            EventType = eventType,
            Insulin = insulin,
            Mills = mills ?? Day1Mills,
        };
    }

    private static Treatment MakeTempBasal(double rate, double durationMinutes, long? mills = null)
    {
        return new Treatment
        {
            EventType = "Temp Basal",
            Rate = rate,
            Duration = durationMinutes,
            Mills = mills ?? Day1Mills,
        };
    }

    #endregion
}
