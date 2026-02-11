using FluentAssertions;
using Nocturne.API.Services;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// Clinical accuracy tests for the StatisticsService.
/// These tests verify that calculations produce correct results using
/// mathematically deterministic inputs and known clinical formulas.
/// </summary>
public class StatisticsServiceClinicalAccuracyTests
{
    private readonly StatisticsService _sut;

    public StatisticsServiceClinicalAccuracyTests()
    {
        _sut = new StatisticsService();
    }

    #region GMI (Glucose Management Indicator) Tests

    /// <summary>
    /// GMI formula: 3.31 + (0.02392 × mean glucose in mg/dL)
    /// Verified against: Bergenstal RM, et al. Diabetes Care. 2018
    /// </summary>
    [Theory]
    [InlineData(100, 5.7)] // 3.31 + (0.02392 * 100) = 5.702 → 5.7
    [InlineData(150, 6.9)] // 3.31 + (0.02392 * 150) = 6.898 → 6.9
    [InlineData(154, 7.0)] // 3.31 + (0.02392 * 154) = 6.9937 → 7.0
    [InlineData(183, 7.7)] // 3.31 + (0.02392 * 183) = 7.6874 → 7.7
    [InlineData(212, 8.4)] // 3.31 + (0.02392 * 212) = 8.381 → 8.4
    [InlineData(240, 9.1)] // 3.31 + (0.02392 * 240) = 9.0508 → 9.1
    public void CalculateGMI_WithKnownMeanGlucose_ShouldMatchPublishedFormula(
        double meanGlucose,
        double expectedGMI
    )
    {
        var result = _sut.CalculateGMI(meanGlucose);

        result.Value.Should().Be(expectedGMI);
        result.MeanGlucose.Should().Be(meanGlucose);
    }

    [Fact]
    public void CalculateGMI_WithZeroGlucose_ShouldReturnZeroWithInsufficientData()
    {
        var result = _sut.CalculateGMI(0);

        result.Value.Should().Be(0);
        result.Interpretation.Should().Be("Insufficient data");
    }

    [Fact]
    public void CalculateGMI_WithNegativeGlucose_ShouldReturnZeroWithInsufficientData()
    {
        var result = _sut.CalculateGMI(-50);

        result.Value.Should().Be(0);
        result.Interpretation.Should().Be("Insufficient data");
    }

    [Theory]
    [InlineData(90, "Non-diabetic range")]         // GMI 5.5 < 5.7 → Non-diabetic
    [InlineData(120, "Prediabetes range")]         // GMI 6.2 → Prediabetes
    [InlineData(150, "Well-controlled diabetes")]  // GMI 6.9 → Well-controlled
    [InlineData(183, "Moderate control")]          // GMI 7.7 → Moderate
    [InlineData(212, "Suboptimal control")]        // GMI 8.4 → Suboptimal
    [InlineData(300, "Poor control - intervention recommended")] // GMI 10.5 → Poor
    public void CalculateGMI_ShouldReturnCorrectInterpretation(
        double meanGlucose,
        string expectedInterpretation
    )
    {
        var result = _sut.CalculateGMI(meanGlucose);

        result.Interpretation.Should().Be(expectedInterpretation);
    }

    #endregion

    #region GRI (Glycemic Risk Index) Tests

    /// <summary>
    /// GRI formula: (3.0 × VLow%) + (2.4 × Low%) + (1.6 × VHigh%) + (0.8 × High%)
    /// Verified against: Klonoff DC, et al. J Diabetes Sci Technol. 2023
    /// </summary>
    [Fact]
    public void CalculateGRI_PerfectControl_ShouldBeZero()
    {
        var tir = new TimeInRangeMetrics
        {
            Percentages = new TimeInRangePercentages
            {
                SevereLow = 0,
                Low = 0,
                Target = 100,
                High = 0,
                SevereHigh = 0,
            },
        };

        var result = _sut.CalculateGRI(tir);

        result.Score.Should().Be(0);
        result.HypoglycemiaComponent.Should().Be(0);
        result.HyperglycemiaComponent.Should().Be(0);
        result.Zone.Should().Be(GRIZone.A);
    }

    [Fact]
    public void CalculateGRI_KnownDistribution_ShouldMatchFormula()
    {
        // 2% severe low, 3% low, 60% target, 25% high, 10% severe high
        // GRI = (3.0 * 2) + (2.4 * 3) + (1.6 * 10) + (0.8 * 25)
        //     = 6 + 7.2 + 16 + 20 = 49.2
        var tir = new TimeInRangeMetrics
        {
            Percentages = new TimeInRangePercentages
            {
                SevereLow = 2,
                Low = 3,
                Target = 60,
                High = 25,
                SevereHigh = 10,
            },
        };

        var result = _sut.CalculateGRI(tir);

        result.Score.Should().Be(49.2);
        result.HypoglycemiaComponent.Should().Be(13.2); // (3.0 * 2) + (2.4 * 3)
        result.HyperglycemiaComponent.Should().Be(36);   // (1.6 * 10) + (0.8 * 25)
        result.Zone.Should().Be(GRIZone.C);
    }

    [Theory]
    [InlineData(0, 0, 100, 0, 0, GRIZone.A)]   // Perfect → Zone A (GRI = 0)
    [InlineData(0, 1, 84, 10, 5, GRIZone.A)]    // (3*0)+(2.4*1)+(1.6*5)+(0.8*10) = 18.4 → Zone A
    [InlineData(1, 4, 60, 25, 10, GRIZone.C)]   // (3*1)+(2.4*4)+(1.6*10)+(0.8*25) = 48.6 → Zone C
    [InlineData(3, 5, 40, 30, 22, GRIZone.E)]   // (3*3)+(2.4*5)+(1.6*22)+(0.8*30) = 80.2 → Zone E
    public void CalculateGRI_ShouldReturnCorrectZone(
        double severeLow,
        double low,
        double target,
        double high,
        double severeHigh,
        GRIZone expectedZone
    )
    {
        var tir = new TimeInRangeMetrics
        {
            Percentages = new TimeInRangePercentages
            {
                SevereLow = severeLow,
                Low = low,
                Target = target,
                High = high,
                SevereHigh = severeHigh,
            },
        };

        var result = _sut.CalculateGRI(tir);

        result.Zone.Should().Be(expectedZone);
    }

    [Fact]
    public void CalculateGRI_ShouldCapAt100()
    {
        // Extreme case: 20% severe low, 20% low, 0% target, 30% high, 30% severe high
        // GRI = (3.0 * 20) + (2.4 * 20) + (1.6 * 30) + (0.8 * 30)
        //     = 60 + 48 + 48 + 24 = 180 → capped at 100
        var tir = new TimeInRangeMetrics
        {
            Percentages = new TimeInRangePercentages
            {
                SevereLow = 20,
                Low = 20,
                Target = 0,
                High = 30,
                SevereHigh = 30,
            },
        };

        var result = _sut.CalculateGRI(tir);

        result.Score.Should().Be(100);
        result.Zone.Should().Be(GRIZone.E);
    }

    #endregion

    #region Estimated A1C Tests

    /// <summary>
    /// eA1C formula: (averageGlucose + 46.7) / 28.7
    /// Based on: Nathan DM et al. "Translating the A1C Assay Into Estimated Average Glucose Values"
    /// </summary>
    [Theory]
    [InlineData(154, 7.0)]   // (154 + 46.7) / 28.7 = 6.993...
    [InlineData(126, 6.0)]   // (126 + 46.7) / 28.7 = 6.017...
    [InlineData(183, 8.0)]   // (183 + 46.7) / 28.7 = 8.003...
    [InlineData(212, 9.0)]   // (212 + 46.7) / 28.7 = 9.013...
    [InlineData(240, 9.99)]  // (240 + 46.7) / 28.7 = 9.988...
    public void CalculateEstimatedA1C_WithKnownGlucose_ShouldMatchFormula(
        double averageGlucose,
        double expectedA1C
    )
    {
        var result = _sut.CalculateEstimatedA1C(averageGlucose);

        result.Should().BeApproximately(expectedA1C, 0.05);
    }

    [Fact]
    public void CalculateEstimatedA1C_WithZero_ShouldReturnZero()
    {
        var result = _sut.CalculateEstimatedA1C(0);

        result.Should().Be(0);
    }

    #endregion

    #region Time In Range Accuracy Tests

    [Fact]
    public void CalculateTimeInRange_WithKnownDistribution_ShouldReturnExactPercentages()
    {
        // Create 100 entries with a known distribution using default thresholds:
        // SevereLow: <54, Low: 54-70, Target: 70-180, High: 180-250, SevereHigh: >250
        var entries = new List<Entry>();

        // 5 entries at 40 mg/dL (severe low)
        entries.AddRange(Enumerable.Range(0, 5).Select(_ => new Entry { Sgv = 40 }));
        // 10 entries at 60 mg/dL (low: 54 <= x < 70)
        entries.AddRange(Enumerable.Range(0, 10).Select(_ => new Entry { Sgv = 60 }));
        // 70 entries at 120 mg/dL (target: 70 <= x <= 180)
        entries.AddRange(Enumerable.Range(0, 70).Select(_ => new Entry { Sgv = 120 }));
        // 10 entries at 200 mg/dL (high: 180 < x <= 250)
        entries.AddRange(Enumerable.Range(0, 10).Select(_ => new Entry { Sgv = 200 }));
        // 5 entries at 300 mg/dL (severe high: > 250)
        entries.AddRange(Enumerable.Range(0, 5).Select(_ => new Entry { Sgv = 300 }));

        var result = _sut.CalculateTimeInRange(entries);

        result.Percentages.SevereLow.Should().Be(5);
        result.Percentages.Low.Should().Be(10);
        result.Percentages.Target.Should().Be(70);
        result.Percentages.High.Should().Be(10);
        result.Percentages.SevereHigh.Should().Be(5);
    }

    [Fact]
    public void CalculateTimeInRange_PercentagesShouldSumTo100()
    {
        var entries = new[]
        {
            new Entry { Sgv = 40 },  // severe low
            new Entry { Sgv = 60 },  // low
            new Entry { Sgv = 100 }, // target
            new Entry { Sgv = 200 }, // high
            new Entry { Sgv = 300 }, // severe high
        };

        var result = _sut.CalculateTimeInRange(entries);

        var totalPercentage =
            result.Percentages.SevereLow
            + result.Percentages.Low
            + result.Percentages.Target
            + result.Percentages.High
            + result.Percentages.SevereHigh;

        totalPercentage.Should().BeApproximately(100, 0.1);
    }

    [Fact]
    public void CalculateTimeInRange_DurationsShouldMatchReadingCount()
    {
        // 20 entries * 5 min interval = 100 minutes total
        var entries = Enumerable.Range(0, 20).Select(_ => new Entry { Sgv = 120 }).ToArray();

        var result = _sut.CalculateTimeInRange(entries);

        // All in target (70-180 default), so all 100 minutes should be in target
        result.Durations.Target.Should().Be(100); // 20 readings * 5 min
        result.Durations.SevereLow.Should().Be(0);
        result.Durations.Low.Should().Be(0);
        result.Durations.High.Should().Be(0);
        result.Durations.SevereHigh.Should().Be(0);
    }

    [Fact]
    public void CalculateTimeInRange_WithCustomThresholds_ShouldApplyCorrectly()
    {
        // Pregnancy thresholds: target 63-140
        var thresholds = new GlycemicThresholds
        {
            SevereLow = 54,
            Low = 63,
            TargetBottom = 63,
            TargetTop = 140,
            High = 140,
            SevereHigh = 250,
        };

        var entries = new[]
        {
            new Entry { Sgv = 100 }, // in target (63-140)
            new Entry { Sgv = 150 }, // high (140-250) with pregnancy thresholds
            new Entry { Sgv = 60 },  // low (54-63)
        };

        var result = _sut.CalculateTimeInRange(entries, thresholds);

        // 1 of 3 in target = 33.33%
        result.Percentages.Target.Should().BeApproximately(33.33, 0.1);
    }

    [Fact]
    public void CalculateTimeInRange_BoundaryValues_ShouldCategorizeCorrectly()
    {
        // Test exact boundary values with default thresholds
        var entries = new[]
        {
            new Entry { Sgv = 54 },  // >= 54 and < 70 → Low (not severe low)
            new Entry { Sgv = 70 },  // >= 70 and <= 180 → Target
            new Entry { Sgv = 180 }, // >= 70 and <= 180 → Target
            new Entry { Sgv = 250 }, // > 180 and <= 250 → High
        };

        var result = _sut.CalculateTimeInRange(entries);

        result.Percentages.SevereLow.Should().Be(0);
        result.Percentages.Low.Should().Be(25);     // 1/4 = 25%
        result.Percentages.Target.Should().Be(50);   // 2/4 = 50%
        result.Percentages.High.Should().Be(25);     // 1/4 = 25%
        result.Percentages.SevereHigh.Should().Be(0);
    }

    [Fact]
    public void CalculateTimeInRange_Episodes_ShouldCountTransitions()
    {
        // Create a pattern: target → low → target → low → target
        // Should count 2 low episodes
        var entries = new[]
        {
            new Entry { Sgv = 120 },
            new Entry { Sgv = 60 },  // low episode 1 starts
            new Entry { Sgv = 60 },
            new Entry { Sgv = 120 }, // back to target
            new Entry { Sgv = 55 },  // low episode 2 starts
            new Entry { Sgv = 120 },
        };

        var result = _sut.CalculateTimeInRange(entries);

        result.Episodes.Low.Should().Be(2);
    }

    #endregion

    #region Basic Statistics Precision Tests

    [Fact]
    public void CalculateBasicStats_ShouldComputeStandardDeviationCorrectly()
    {
        // Using well-known dataset: {2, 4, 4, 4, 5, 5, 7, 9}
        // Population mean = 5, but these are out of valid glucose range.
        // Use glucose-range values: {100, 120, 120, 120, 130, 130, 150, 170}
        // Mean = 130
        // Sample variance = Σ(x-mean)² / (n-1)
        // = (900 + 100 + 100 + 100 + 0 + 0 + 400 + 1600) / 7
        // = 3200 / 7 = 457.14...
        // Sample SD = sqrt(457.14) = 21.38...
        var values = new double[] { 100, 120, 120, 120, 130, 130, 150, 170 };

        var result = _sut.CalculateBasicStats(values);

        result.Count.Should().Be(8);
        result.Mean.Should().Be(130);
        result.Median.Should().Be(125); // average of 120 and 130
        result.Min.Should().Be(100);
        result.Max.Should().Be(170);
        result.StandardDeviation.Should().BeApproximately(21.4, 0.1);
    }

    [Fact]
    public void CalculateBasicStats_WithSingleValue_ShouldReturnZeroStdDev()
    {
        var values = new double[] { 120 };

        var result = _sut.CalculateBasicStats(values);

        result.Count.Should().Be(1);
        result.Mean.Should().Be(120);
        result.Median.Should().Be(120);
        result.StandardDeviation.Should().Be(0);
    }

    [Fact]
    public void CalculateBasicStats_ShouldFilterOutOfRangeValues()
    {
        // Valid glucose range is > 0 and < 600
        var values = new double[] { -10, 0, 100, 200, 600, 700 };

        var result = _sut.CalculateBasicStats(values);

        result.Count.Should().Be(2); // Only 100 and 200
        result.Mean.Should().Be(150);
        result.Min.Should().Be(100);
        result.Max.Should().Be(200);
    }

    [Fact]
    public void CalculateBasicStats_Percentiles_ShouldBeCorrect()
    {
        // 100 evenly distributed values from 1 to 100 (all valid glucose range)
        // Actually, valid range is > 0 and < 600, so 1-100 would all get filtered since < 0 threshold...
        // Looking at code: v > 0 && v < 600, so 1-100 are all valid
        // But wait, the existing test uses values 70-160, so let me use glucose-realistic values
        var values = Enumerable.Range(70, 131).Select(x => (double)x).ToArray(); // 70 to 200

        var result = _sut.CalculateBasicStats(values);

        result.Count.Should().Be(131);
        result.Mean.Should().Be(135); // (70+200)/2 = 135
        result.Median.Should().Be(135); // Middle value of 70..200
        result.Percentiles.P25.Should().BeApproximately(102.5, 0.5);
        result.Percentiles.P50.Should().Be(135);
        result.Percentiles.P75.Should().BeApproximately(167.5, 0.5);
    }

    [Fact]
    public void CalculateBasicStats_OddCount_MedianShouldBeMiddleValue()
    {
        var values = new double[] { 100, 120, 140 };

        var result = _sut.CalculateBasicStats(values);

        result.Median.Should().Be(120);
    }

    [Fact]
    public void CalculateBasicStats_EvenCount_MedianShouldBeAverageOfTwoMiddle()
    {
        var values = new double[] { 100, 120, 140, 160 };

        var result = _sut.CalculateBasicStats(values);

        result.Median.Should().Be(130); // (120 + 140) / 2
    }

    #endregion

    #region HBGI / LBGI Tests

    /// <summary>
    /// HBGI and LBGI use the Kovatchev formula:
    /// f(BG) = 1.084 * (ln(BG/18)^1.084 - 1.928)
    /// HBGI: average of 10 * f(BG)^2 for all readings where f(BG) > 0
    /// LBGI: average of 10 * f(BG)^2 for all readings where f(BG) < 0
    /// </summary>
    [Fact]
    public void CalculateHBGI_AllInRange_ShouldBeLow()
    {
        // Readings all at 100 mg/dL - f(BG) near zero, HBGI should be very low
        var values = Enumerable.Repeat(100.0, 100).ToArray();

        var result = _sut.CalculateHBGI(values);

        result.Should().BeLessThan(4.5, "HBGI <= 4.5 indicates low hyperglycemia risk");
    }

    [Fact]
    public void CalculateHBGI_HighValues_ShouldBeElevated()
    {
        // Readings all at 300 mg/dL - clearly elevated HBGI
        var values = Enumerable.Repeat(300.0, 100).ToArray();

        var result = _sut.CalculateHBGI(values);

        result.Should().BeGreaterThan(9.0, "HBGI > 9.0 indicates high hyperglycemia risk");
    }

    [Fact]
    public void CalculateLBGI_AllInRange_ShouldBeMinimal()
    {
        // Readings all at 100 mg/dL - LBGI should be very low
        var values = Enumerable.Repeat(100.0, 100).ToArray();

        var result = _sut.CalculateLBGI(values);

        result.Should().BeLessThan(1.1, "LBGI <= 1.1 indicates minimal hypoglycemia risk");
    }

    [Fact]
    public void CalculateLBGI_LowValues_ShouldBeElevated()
    {
        // Readings all at 55 mg/dL - clearly elevated LBGI
        var values = Enumerable.Repeat(55.0, 100).ToArray();

        var result = _sut.CalculateLBGI(values);

        result.Should().BeGreaterThan(2.5, "LBGI > 2.5 indicates moderate+ hypoglycemia risk");
    }

    [Fact]
    public void CalculateHBGI_ShouldBeZero_WhenAllValuesAreBelow112()
    {
        // At glucose ~112 mg/dL (6.22 mmol), f(BG) ≈ 0
        // Below ~112, f(BG) < 0, so HBGI contribution = 0
        var values = Enumerable.Repeat(70.0, 50).ToArray();

        var result = _sut.CalculateHBGI(values);

        result.Should().Be(0, "all low values contribute 0 to HBGI");
    }

    [Fact]
    public void CalculateLBGI_ShouldBeZero_WhenAllValuesAreAbove112()
    {
        // Above ~112, f(BG) > 0, so LBGI contribution = 0
        var values = Enumerable.Repeat(200.0, 50).ToArray();

        var result = _sut.CalculateLBGI(values);

        result.Should().Be(0, "all high values contribute 0 to LBGI");
    }

    #endregion

    #region Insulin Delivery Statistics Tests

    [Fact]
    public void CalculateInsulinDeliveryStatistics_WithMixedTreatments_ShouldCategorizeCorrectly()
    {
        var startDate = new DateTime(2024, 1, 1);
        var endDate = new DateTime(2024, 1, 8); // 7 days

        var treatments = new[]
        {
            // Meal boluses
            new Treatment { EventType = "Meal Bolus", Insulin = 5.0, Carbs = 45, Mills = DateTimeOffset.Parse("2024-01-02T08:00:00Z").ToUnixTimeMilliseconds() },
            new Treatment { EventType = "Meal Bolus", Insulin = 7.0, Carbs = 60, Mills = DateTimeOffset.Parse("2024-01-02T12:00:00Z").ToUnixTimeMilliseconds() },
            new Treatment { EventType = "Snack Bolus", Insulin = 2.0, Carbs = 15, Mills = DateTimeOffset.Parse("2024-01-02T15:00:00Z").ToUnixTimeMilliseconds() },

            // Correction boluses (no carbs)
            new Treatment { EventType = "Correction Bolus", Insulin = 1.5, Mills = DateTimeOffset.Parse("2024-01-02T20:00:00Z").ToUnixTimeMilliseconds() },

            // Basal (non-bolus with insulin)
            new Treatment { EventType = "Temp Basal", Insulin = 0.5, Mills = DateTimeOffset.Parse("2024-01-02T06:00:00Z").ToUnixTimeMilliseconds() },
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(treatments, startDate, endDate);

        // Bolus = 5 + 7 + 2 + 1.5 = 15.5
        result.TotalBolus.Should().Be(15.5);
        // Basal = 0.5
        result.TotalBasal.Should().Be(0.5);
        // Total = 16
        result.TotalInsulin.Should().Be(16.0);
        // Carbs = 45 + 60 + 15 = 120
        result.TotalCarbs.Should().Be(120);
        // 4 bolus treatments
        result.BolusCount.Should().Be(4);
        // 1 basal treatment
        result.BasalCount.Should().Be(1);
        // Meal boluses: "Meal Bolus" and "Snack Bolus" → 3
        result.MealBoluses.Should().Be(3);
        // Correction boluses: 1
        result.CorrectionBoluses.Should().Be(1);
        // Carb count: 3 treatments with carbs
        result.CarbCount.Should().Be(3);
        // Carb+bolus: 3 treatments have both carbs and bolus insulin
        result.CarbBolusCount.Should().Be(3);
        // I:C ratio: 120 / 15.5 ≈ 7.7
        result.IcRatio.Should().BeApproximately(7.7, 0.1);
        // Day count = 7
        result.DayCount.Should().Be(7);
        // TDD = 16 / 7 ≈ 2.3
        result.Tdd.Should().BeApproximately(2.3, 0.1);
    }

    [Fact]
    public void CalculateInsulinDeliveryStatistics_WithNoTreatments_ShouldReturnZeroes()
    {
        var result = _sut.CalculateInsulinDeliveryStatistics(
            Array.Empty<Treatment>(),
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 8)
        );

        result.TotalBolus.Should().Be(0);
        result.TotalBasal.Should().Be(0);
        result.TotalInsulin.Should().Be(0);
        result.BolusCount.Should().Be(0);
        result.BasalPercent.Should().Be(0);
        result.BolusPercent.Should().Be(0);
    }

    [Fact]
    public void CalculateInsulinDeliveryStatistics_PercentagesShouldSumTo100()
    {
        var treatments = new[]
        {
            new Treatment { EventType = "Meal Bolus", Insulin = 8.0, Mills = DateTimeOffset.Parse("2024-01-02T12:00:00Z").ToUnixTimeMilliseconds() },
            new Treatment { EventType = "Temp Basal", Insulin = 12.0, Mills = DateTimeOffset.Parse("2024-01-02T06:00:00Z").ToUnixTimeMilliseconds() },
        };

        var result = _sut.CalculateInsulinDeliveryStatistics(
            treatments,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2)
        );

        (result.BasalPercent + result.BolusPercent).Should().BeApproximately(100, 0.1);
        result.BolusPercent.Should().BeApproximately(40, 0.1); // 8/20 * 100
        result.BasalPercent.Should().BeApproximately(60, 0.1); // 12/20 * 100
    }

    [Fact]
    public void CalculateInsulinDeliveryStatistics_AllBolusTypes_ShouldBeCategorizedAsBolus()
    {
        var bolusTypes = new[]
        {
            "Meal Bolus", "Correction Bolus", "Snack Bolus", "Bolus Wizard",
            "Combo Bolus", "Bolus", "SMB", "e-Bolus", "Extended Bolus", "Dual Wave",
        };

        var treatments = bolusTypes.Select((type, i) => new Treatment
        {
            EventType = type,
            Insulin = 1.0,
            Mills = DateTimeOffset.Parse("2024-01-02T12:00:00Z").ToUnixTimeMilliseconds() + i * 60000,
        }).ToArray();

        var result = _sut.CalculateInsulinDeliveryStatistics(
            treatments,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 3)
        );

        result.BolusCount.Should().Be(10);
        result.BasalCount.Should().Be(0);
        result.TotalBolus.Should().Be(10);
    }

    #endregion

    #region Treatment Summary Tests

    [Fact]
    public void CalculateTreatmentSummary_BasalFromRateAndDuration_ShouldCalculateInsulin()
    {
        // Rate = 1.2 U/hr, Duration = 30 min → 1.2 * 30 / 60 = 0.6 U
        var treatments = new[]
        {
            new Treatment
            {
                EventType = "Temp Basal",
                Rate = 1.2,
                Duration = 30,
            },
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        result.Totals.Insulin.Basal.Should().BeApproximately(0.6, 0.01);
        result.Totals.Insulin.Bolus.Should().Be(0);
    }

    [Fact]
    public void CalculateTreatmentSummary_CarbToInsulinRatio_ShouldBeCorrect()
    {
        var treatments = new[]
        {
            new Treatment { EventType = "Meal Bolus", Insulin = 5.0, Carbs = 50 },
            new Treatment { EventType = "Meal Bolus", Insulin = 3.0, Carbs = 30 },
        };

        var result = _sut.CalculateTreatmentSummary(treatments);

        // Total carbs = 80, Total insulin (all bolus) = 8
        // C:I ratio = 80/8 = 10.0
        result.CarbToInsulinRatio.Should().Be(10);
    }

    #endregion

    #region Clinical Target Assessment Tests

    [Fact]
    public void AssessAgainstTargets_AllTargetsMet_ShouldReturnExcellent()
    {
        var analytics = CreateAnalyticsWithTIR(
            severeLow: 0, low: 2, target: 80, high: 15, severeHigh: 3, cv: 30
        );

        var result = _sut.AssessAgainstTargets(analytics, DiabetesPopulation.Type1Adult);

        result.TargetsMet.Should().Be(6);
        result.TotalTargets.Should().Be(6);
        result.OverallAssessment.Should().Be("Excellent");
        result.TIRAssessment.Status.Should().Be(TargetStatus.Met);
        result.TBRAssessment.Status.Should().Be(TargetStatus.Met);
        result.VeryLowAssessment.Status.Should().Be(TargetStatus.Met);
        result.TARAssessment.Status.Should().Be(TargetStatus.Met);
        result.VeryHighAssessment.Status.Should().Be(TargetStatus.Met);
        result.CVAssessment.Status.Should().Be(TargetStatus.Met);
    }

    [Fact]
    public void AssessAgainstTargets_NoTargetsMet_ShouldReturnNeedsSignificantImprovement()
    {
        // TIR < 70%, TBR > 4%, VLow > 1%, TAR > 25%, VHigh > 5%, CV > 36%
        var analytics = CreateAnalyticsWithTIR(
            severeLow: 5, low: 10, target: 40, high: 30, severeHigh: 15, cv: 50
        );

        var result = _sut.AssessAgainstTargets(analytics, DiabetesPopulation.Type1Adult);

        result.TargetsMet.Should().Be(0);
        result.OverallAssessment.Should().Be("Needs Significant Improvement");
    }

    [Fact]
    public void AssessAgainstTargets_ElderlyPopulation_ShouldUseDifferentTargets()
    {
        // Elderly has relaxed targets: TIR >= 50%, TBR <= 1%, VLow <= 0%
        var analytics = CreateAnalyticsWithTIR(
            severeLow: 0, low: 0, target: 55, high: 35, severeHigh: 10, cv: 30
        );

        var result = _sut.AssessAgainstTargets(analytics, DiabetesPopulation.Elderly);

        // TIR 55% >= 50% → Met
        result.TIRAssessment.Status.Should().Be(TargetStatus.Met);
        // TBR 0% <= 1% → Met
        result.TBRAssessment.Status.Should().Be(TargetStatus.Met);
        // TAR 45% <= 50% → Met
        result.TARAssessment.Status.Should().Be(TargetStatus.Met);
    }

    [Fact]
    public void AssessAgainstTargets_CloseToTarget_ShouldReturnClose()
    {
        // TIR at 63% (>= 70 * 0.9 = 63) → Close
        var analytics = CreateAnalyticsWithTIR(
            severeLow: 0, low: 2, target: 65, high: 25, severeHigh: 8, cv: 30
        );

        var result = _sut.AssessAgainstTargets(analytics, DiabetesPopulation.Type1Adult);

        result.TIRAssessment.Status.Should().Be(TargetStatus.Close);
    }

    [Fact]
    public void AssessAgainstTargets_ShouldGenerateActionableInsights()
    {
        var analytics = CreateAnalyticsWithTIR(
            severeLow: 3, low: 5, target: 50, high: 30, severeHigh: 12, cv: 45
        );

        var result = _sut.AssessAgainstTargets(analytics, DiabetesPopulation.Type1Adult);

        result.PriorityAreas.Should().NotBeEmpty();
        result.ActionableInsights.Should().NotBeEmpty();
        // Should flag severe hypoglycemia since VeryLow > 1%
        result.PriorityAreas.Should().Contain(p => p.Contains("severe hypoglycemia"));
    }

    #endregion

    #region Data Sufficiency Assessment Tests

    [Fact]
    public void AssessDataSufficiency_SufficientData_ShouldReturnTrue()
    {
        // 14 days * 288 readings/day = 4032 expected
        // 70% of 4032 = 2822
        var baseTime = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 3000).Select(i => new Entry
        {
            Sgv = 120,
            Mills = baseTime.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        });

        var result = _sut.AssessDataSufficiency(entries, days: 14);

        result.IsSufficient.Should().BeTrue();
        result.ActualReadings.Should().Be(3000);
        result.CompletenessPercentage.Should().BeGreaterThan(70);
    }

    [Fact]
    public void AssessDataSufficiency_InsufficientData_ShouldReturnFalse()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 100).Select(i => new Entry
        {
            Sgv = 120,
            Mills = baseTime.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        });

        var result = _sut.AssessDataSufficiency(entries, days: 14);

        result.IsSufficient.Should().BeFalse();
        result.WarningMessage.Should().NotBeEmpty();
    }

    [Fact]
    public void AssessDataSufficiency_NoData_ShouldReturnFalseWithMessage()
    {
        var result = _sut.AssessDataSufficiency(Array.Empty<Entry>(), days: 14);

        result.IsSufficient.Should().BeFalse();
        result.DaysWithData.Should().Be(0);
        result.ActualReadings.Should().Be(0);
        result.CompletenessPercentage.Should().Be(0);
        result.WarningMessage.Should().Contain("No glucose data");
    }

    [Fact]
    public void AssessDataSufficiency_LargeGap_ShouldWarn()
    {
        // Sufficient data but with a 13-hour gap
        var baseTime = DateTimeOffset.UtcNow;
        var entries = new List<Entry>();

        // First batch: 2000 readings over ~7 days
        entries.AddRange(Enumerable.Range(0, 2000).Select(i => new Entry
        {
            Sgv = 120,
            Mills = baseTime.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        }));

        // Then a 13-hour gap, then more readings
        var afterGap = baseTime.AddMinutes(2000 * 5 + 13 * 60);
        entries.AddRange(Enumerable.Range(0, 1500).Select(i => new Entry
        {
            Sgv = 120,
            Mills = afterGap.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        }));

        var result = _sut.AssessDataSufficiency(entries, days: 14);

        result.LongestGapHours.Should().BeGreaterThan(12);
        result.WarningMessage.Should().Contain("gap");
    }

    #endregion

    #region Unit Conversion Tests

    /// <summary>
    /// Conversion factor: 1 mmol/L = 18.0182 mg/dL
    /// </summary>
    [Theory]
    [InlineData(90, 5.0)]
    [InlineData(180, 10.0)]
    [InlineData(270, 15.0)]
    public void MgdlToMMOL_ShouldConvertCorrectly(double mgdl, double expectedMmol)
    {
        var result = _sut.MgdlToMMOL(mgdl);

        result.Should().BeApproximately(expectedMmol, 0.1);
    }

    [Theory]
    [InlineData(5.0, 90)]
    [InlineData(10.0, 180)]
    [InlineData(15.0, 270)]
    public void MmolToMGDL_ShouldConvertCorrectly(double mmol, double expectedMgdl)
    {
        var result = _sut.MmolToMGDL(mmol);

        result.Should().BeApproximately(expectedMgdl, 1);
    }

    [Fact]
    public void UnitConversions_ShouldRoundTrip()
    {
        var originalMgdl = 150.0;
        var mmol = _sut.MgdlToMMOL(originalMgdl);
        var backToMgdl = _sut.MmolToMGDL(mmol);

        backToMgdl.Should().BeApproximately(originalMgdl, 1);
    }

    #endregion

    #region Extended Analytics Integration Tests

    [Fact]
    public void AnalyzeGlucoseDataExtended_ShouldReturnAllMetrics()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(0, 288).Select(i => new Entry
        {
            Sgv = 100 + (int)(30 * Math.Sin(i * 2 * Math.PI / 288)), // Sinusoidal 70-130
            Mills = baseTime.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        }).ToArray();

        var treatments = new[]
        {
            new Treatment { EventType = "Meal Bolus", Insulin = 5.0, Carbs = 45 },
        };

        var result = _sut.AnalyzeGlucoseDataExtended(entries, treatments);

        // Core analytics should be populated
        result.BasicStats.Count.Should().Be(288);
        result.BasicStats.Mean.Should().BeGreaterThan(0);
        result.TimeInRange.Should().NotBeNull();
        result.GlycemicVariability.Should().NotBeNull();
        result.DataQuality.Should().NotBeNull();

        // Extended metrics should be populated
        result.GMI.Should().NotBeNull();
        result.GMI.Value.Should().BeGreaterThan(0);
        result.GRI.Should().NotBeNull();
        result.ClinicalAssessment.Should().NotBeNull();
        result.DataSufficiency.Should().NotBeNull();

        // Treatment summary should be included
        result.TreatmentSummary.Should().NotBeNull();
        result.TreatmentSummary!.Totals.Insulin.Bolus.Should().Be(5.0);
    }

    [Fact]
    public void AnalyzeGlucoseDataExtended_WithEmptyData_ShouldReturnSafeDefaults()
    {
        var result = _sut.AnalyzeGlucoseDataExtended(
            Array.Empty<Entry>(),
            Array.Empty<Treatment>()
        );

        result.BasicStats.Count.Should().Be(0);
        result.GMI.Value.Should().Be(0);
        result.GRI.Score.Should().Be(0);
    }

    #endregion

    #region Glucose Distribution Tests

    [Fact]
    public void CalculateGlucoseDistribution_PercentagesShouldSumTo100()
    {
        var entries = new[]
        {
            new Entry { Sgv = 75 },
            new Entry { Sgv = 95 },
            new Entry { Sgv = 115 },
            new Entry { Sgv = 135 },
            new Entry { Sgv = 175 },
            new Entry { Sgv = 220 },
            new Entry { Sgv = 280 },
            new Entry { Sgv = 350 },
        };

        var result = _sut.CalculateGlucoseDistribution(entries).ToList();

        result.Sum(d => d.Percent).Should().BeApproximately(100, 0.01);
    }

    [Fact]
    public void CalculateGlucoseDistribution_WithCustomBins_ShouldUseCustomRanges()
    {
        var customBins = new[]
        {
            new DistributionBin { Range = "Low", Min = 0, Max = 70 },
            new DistributionBin { Range = "Normal", Min = 71, Max = 140 },
            new DistributionBin { Range = "High", Min = 141, Max = 9999 },
        };

        var entries = new[]
        {
            new Entry { Sgv = 60 },  // Low
            new Entry { Sgv = 100 }, // Normal
            new Entry { Sgv = 120 }, // Normal
            new Entry { Sgv = 200 }, // High
        };

        var result = _sut.CalculateGlucoseDistribution(entries, customBins).ToList();

        result.Should().HaveCount(3);
        result.First(r => r.Range == "Low").Count.Should().Be(1);
        result.First(r => r.Range == "Normal").Count.Should().Be(2);
        result.First(r => r.Range == "High").Count.Should().Be(1);
    }

    #endregion

    #region Glycemic Variability Coefficient of Variation Tests

    [Fact]
    public void CalculateGlycemicVariability_StableGlucose_ShouldHaveLowCV()
    {
        // All values around 100 with minimal variation
        var values = new double[] { 98, 100, 102, 99, 101, 100, 99, 101 };
        var entries = values.Select((v, i) => new Entry
        {
            Sgv = v,
            Mills = DateTimeOffset.UtcNow.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        });

        var result = _sut.CalculateGlycemicVariability(values, entries);

        // CV < 36% is the clinical target
        result.CoefficientOfVariation.Should().BeLessThan(36);
        result.CoefficientOfVariation.Should().BeLessThan(5, "nearly identical values should have very low CV");
    }

    [Fact]
    public void CalculateGlycemicVariability_HighlyVariable_ShouldHaveHighCV()
    {
        // Wild swings: 50, 300, 50, 300, etc.
        var values = new double[] { 50, 300, 50, 300, 50, 300, 50, 300 };
        var entries = values.Select((v, i) => new Entry
        {
            Sgv = v,
            Mills = DateTimeOffset.UtcNow.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        });

        var result = _sut.CalculateGlycemicVariability(values, entries);

        result.CoefficientOfVariation.Should().BeGreaterThan(50);
    }

    #endregion

    #region Averaged Stats (Hourly) Tests

    [Fact]
    public void CalculateAveragedStats_ShouldReturn24Hours()
    {
        var entries = Enumerable.Range(0, 48).Select(i => new Entry
        {
            Sgv = 100 + (i % 24) * 2,
            Date = DateTime.Today.AddMinutes(i * 30), // 2 readings per hour
        });

        var result = _sut.CalculateAveragedStats(entries).ToList();

        result.Should().HaveCount(24);
        result.Select(r => r.Hour).Should().BeEquivalentTo(Enumerable.Range(0, 24));
    }

    [Fact]
    public void CalculateAveragedStats_ShouldAverageMultipleReadingsPerHour()
    {
        var entries = new[]
        {
            new Entry { Sgv = 100, Date = DateTime.Today.AddHours(8).AddMinutes(0) },
            new Entry { Sgv = 120, Date = DateTime.Today.AddHours(8).AddMinutes(15) },
            new Entry { Sgv = 140, Date = DateTime.Today.AddHours(8).AddMinutes(30) },
        };

        var result = _sut.CalculateAveragedStats(entries).ToList();

        var hour8 = result.First(r => r.Hour == 8);
        hour8.Count.Should().Be(3);
        // Mean of 100, 120, 140 = 120, rounded to 1 decimal
        hour8.Mean.Should().Be(120);
    }

    #endregion

    #region J-Index Tests

    [Fact]
    public void CalculateJIndex_PerfectControl_ShouldBeVeryLow()
    {
        // Values all at 112 (target mean) → J-Index should be near 0
        var values = Enumerable.Repeat(112.0, 100).ToArray();
        var mean = 112.0;

        var result = _sut.CalculateJIndex(values, mean);

        // Mean component: 0.324 * (112 - 112)^2 = 0
        // Variability component: 0.0018 * 0 = 0
        result.Should().Be(0);
    }

    [Fact]
    public void CalculateJIndex_HighMeanAndVariability_ShouldBeElevated()
    {
        var values = new double[] { 60, 100, 200, 250, 300 };
        var mean = values.Average(); // 182

        var result = _sut.CalculateJIndex(values, mean);

        // Should be significantly elevated with high mean and high variance
        result.Should().BeGreaterThan(10);
    }

    #endregion

    #region PGS (Patient Glycemic Status) Tests

    [Fact]
    public void CalculatePGS_ExcellentControl_ShouldBeLow()
    {
        // All values in range (70-180), GVI = 1.0, mean = 100
        var values = Enumerable.Repeat(100.0, 100).ToArray();

        // With 100% in range, PGS = GVI * mean * (1 - 1.0) = 0
        var result = _sut.CalculatePGS(values, 1.0, 100);

        result.Should().Be(0, "100% TIR with GVI=1 should yield PGS=0");
    }

    [Fact]
    public void CalculatePGS_PoorControl_ShouldBeHigh()
    {
        // 50% of values out of range
        var inRange = Enumerable.Repeat(100.0, 50);
        var outRange = Enumerable.Repeat(250.0, 50);
        var values = inRange.Concat(outRange).ToArray();
        var mean = values.Average(); // 175

        // PGS = GVI * mean * (1 - TIR%)
        // TIR = 50/100 = 0.5
        // PGS = 1.5 * 175 * 0.5 = 131.25
        var result = _sut.CalculatePGS(values, 1.5, mean);

        result.Should().BeApproximately(131.25, 0.1);
        result.Should().BeGreaterThan(100, "PGS > 100 indicates poor glycemic status");
    }

    #endregion

    #region Standard Deviation Consistency Tests

    /// <summary>
    /// All SD calculations should use sample SD (Bessel's correction, N-1).
    /// CGM data is always a sample — population SD underestimates variability
    /// and could make a patient's CV appear lower than it actually is.
    /// </summary>
    [Fact]
    public void StandardDeviation_BasicStatsAndGlycemicVariability_ShouldAgree()
    {
        var values = new double[] { 80, 100, 120, 140, 160 };
        var entries = values.Select((v, i) => new Entry
        {
            Sgv = v,
            Mills = DateTimeOffset.UtcNow.AddMinutes(i * 5).ToUnixTimeMilliseconds(),
        }).ToArray();

        var basicStats = _sut.CalculateBasicStats(values);
        var gv = _sut.CalculateGlycemicVariability(values, entries);

        // Both use sample SD (N-1): sqrt(Σ(x-mean)²/4) = sqrt(4000/4) = sqrt(1000) ≈ 31.6
        basicStats.StandardDeviation.Should().Be(
            gv.StandardDeviation,
            "BasicStats and GlycemicVariability should use the same sample SD formula"
        );
    }

    #endregion

    #region OverallAverages TightTimeInRange Tests

    /// <summary>
    /// AvgTightTimeInRange should use the TightTarget (70-140) percentage,
    /// not the broad Target (70-180) percentage.
    /// </summary>
    [Fact]
    public void OverallAverages_TightTimeInRange_ShouldUseTightTarget()
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
                        Insulin = new InsulinTotals { Bolus = 10 },
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages
                    {
                        Target = 90,       // 90% in 70-180 range
                        TightTarget = 40,  // Only 40% in 70-140 range
                    },
                },
            },
        };

        var result = _sut.CalculateOverallAverages(dayData);

        result!.AvgTightTimeInRange.Should().Be(
            40,
            "AvgTightTimeInRange should use the TightTarget percentage (70-140), not the broad Target (70-180)"
        );
    }

    #endregion

    #region RangeStats SD Consistency Tests

    /// <summary>
    /// RangeStats (from TimeInRange detail) should use sample SD consistent with BasicStats.
    /// </summary>
    [Fact]
    public void RangeStats_StandardDeviation_ShouldMatchBasicStatsFormula()
    {
        // All readings in target (70-180) so Target RangeStats gets all data
        var entries = new[]
        {
            new Entry { Sgv = 80 },
            new Entry { Sgv = 100 },
            new Entry { Sgv = 120 },
            new Entry { Sgv = 140 },
            new Entry { Sgv = 160 },
        };

        var tir = _sut.CalculateTimeInRange(entries);
        var basicStats = _sut.CalculateBasicStats(new double[] { 80, 100, 120, 140, 160 });

        tir.RangeStats.Target.StandardDeviation.Should().Be(
            basicStats.StandardDeviation,
            "RangeStats and BasicStats should use consistent sample SD calculations"
        );
    }

    #endregion

    #region Edge Case: OverallAverages Divides by Different Denominators

    /// <summary>
    /// CalculateOverallAverages divides insulin/food averages by DaysWithData
    /// (only days with insulin > 0), but divides TIR averages by dataPoints.Count
    /// (all days). If some days have glucose data but no insulin, the denominators differ.
    /// This is potentially intentional, but worth documenting/testing.
    /// </summary>
    [Fact]
    public void OverallAverages_MixedDataDays_DenominatorBehavior()
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
                        Insulin = new InsulinTotals { Bolus = 20, Basal = 10 },
                        Food = new FoodTotals { Carbs = 200 },
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages { Target = 80 },
                },
            },
            new DayData
            {
                Date = "2024-01-02",
                TreatmentSummary = new TreatmentSummary
                {
                    // No insulin data this day (e.g. pump disconnected)
                    Totals = new TreatmentTotals
                    {
                        Insulin = new InsulinTotals { Bolus = 0, Basal = 0 },
                        Food = new FoodTotals { Carbs = 0 },
                    },
                },
                TimeInRanges = new TimeInRangeMetrics
                {
                    Percentages = new TimeInRangePercentages { Target = 60 },
                },
            },
        };

        var result = _sut.CalculateOverallAverages(dayData);

        // Insulin averages use DaysWithData (1 day had insulin > 0)
        // So AvgTotalDaily = 30 / 1 = 30
        result!.AvgTotalDaily.Should().Be(30);

        // TIR averages use dataPoints.Count (2 days total)
        // So AvgTimeInRange = (80 + 60) / 2 = 70
        result.AvgTimeInRange.Should().Be(70);

        // This means carb average is also divided by DaysWithData (1)
        // So 200 / 1 = 200, even though there were 2 days
        result.AvgCarbs.Should().Be(200);
    }

    #endregion

    #region Edge Case: Episode Counting Across Severity Boundaries

    /// <summary>
    /// When glucose drops from target → severe low → low → target,
    /// the current implementation counts this as 1 severe low episode + 1 low episode.
    /// Clinically this is a single continuous hypoglycemic event.
    /// </summary>
    [Fact]
    public void TimeInRange_Episodes_SevereLowToLowTransition_CountsSeparateEpisodes()
    {
        var entries = new[]
        {
            new Entry { Sgv = 120 }, // target
            new Entry { Sgv = 45 },  // severe low — episode starts
            new Entry { Sgv = 45 },  // severe low — continues
            new Entry { Sgv = 60 },  // low — this is a DIFFERENT range, counts as new episode
            new Entry { Sgv = 60 },  // low — continues
            new Entry { Sgv = 120 }, // back to target
        };

        var result = _sut.CalculateTimeInRange(entries);

        // Current behavior: counts separate episodes for severity transitions
        // This means one continuous hypo event gets counted as 2 episodes
        result.Episodes.SevereLow.Should().Be(1);
        result.Episodes.Low.Should().Be(1);

        // Total hypo "episodes" reported = 2, but clinically it was 1 event
        var totalHypoEpisodes = result.Episodes.SevereLow + result.Episodes.Low;
        totalHypoEpisodes.Should().Be(2,
            "current implementation counts severity transitions as separate episodes — " +
            "consider whether a single continuous hypo event should be counted as 1 episode");
    }

    #endregion

    #region Helper Methods

    private static GlucoseAnalytics CreateAnalyticsWithTIR(
        double severeLow,
        double low,
        double target,
        double high,
        double severeHigh,
        double cv
    )
    {
        return new GlucoseAnalytics
        {
            TimeInRange = new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages
                {
                    SevereLow = severeLow,
                    Low = low,
                    Target = target,
                    High = high,
                    SevereHigh = severeHigh,
                },
            },
            GlycemicVariability = new GlycemicVariability
            {
                CoefficientOfVariation = cv,
            },
        };
    }

    #endregion
}
