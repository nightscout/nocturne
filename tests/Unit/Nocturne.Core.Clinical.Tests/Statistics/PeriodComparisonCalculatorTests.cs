using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class PeriodComparisonCalculatorTests
{
    private static PeriodComparisonCalculator.PeriodFigures Period(
        Action<ExtendedGlucoseAnalytics>? analysis = null,
        Action<InsulinDeliveryStatistics>? insulin = null,
        PersonalRangeTimeInRange? personalRange = null,
        int readings = 100)
    {
        var analytics = new ExtendedGlucoseAnalytics { GlycemicVariability = new GlycemicVariability() };
        analytics.BasicStats.Count = readings;
        analysis?.Invoke(analytics);

        var delivery = new InsulinDeliveryStatistics { InsulinEventCount = 10, CarbCount = 5 };
        insulin?.Invoke(delivery);

        return new(new ReportAnalysisResult { Analysis = analytics, PersonalRange = personalRange }, delivery);
    }

    private static MetricComparison Metric(PeriodComparison comparison, PeriodComparisonMetric metric) =>
        comparison.Metrics.Single(m => m.Metric == metric);

    private static PeriodComparison Compare(
        PeriodComparisonCalculator.PeriodFigures a,
        PeriodComparisonCalculator.PeriodFigures b,
        GlucoseUnit units = GlucoseUnit.MgDl) =>
        PeriodComparisonCalculator.Build(a, b, units);

    [Fact]
    public void Percentages_AreRoundedToOneDecimal_AndTheChangeIsReadOffTheUnroundedValues()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.Percentages.Target = 61.04),
            Period(a => a.TimeInRange.Percentages.Target = 61.18));

        var tir = Metric(comparison, PeriodComparisonMetric.TirTarget);
        tir.ValueA.Should().Be(61.0);
        tir.ValueB.Should().Be(61.2);
        tir.Delta.Should().Be(0.1);
        tir.ValueDecimals.Should().Be(1);
        tir.DeltaDecimals.Should().Be(1);
        tir.PercentChange.Should().BeApproximately((61.18 - 61.04) / 61.04 * 100, 1e-9);
    }

    [Fact]
    public void Gmi_ValuesAreShownAtOneDecimalAndTheChangeAtTwo()
    {
        var comparison = Compare(
            Period(a => a.GMI.Value = 7.04),
            Period(a => a.GMI.Value = 7.11));

        var gmi = Metric(comparison, PeriodComparisonMetric.Gmi);
        gmi.ValueA.Should().Be(7.0);
        gmi.ValueB.Should().Be(7.1);
        gmi.Delta.Should().Be(0.07);
        gmi.ValueDecimals.Should().Be(1);
        gmi.DeltaDecimals.Should().Be(2);
    }

    [Fact]
    public void MinutesPerDay_AreWholeMinutes_AndAHalfMinuteRoundsAwayFromZero()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.AverageDailyMinutes.Low = 10.0),
            Period(a => a.TimeInRange.AverageDailyMinutes.Low = 9.5));

        var hypo = Metric(comparison, PeriodComparisonMetric.HypoMinutesPerDay);
        hypo.ValueB.Should().Be(10);
        hypo.Delta.Should().Be(-1);
        hypo.DeltaDecimals.Should().Be(0);
    }

    [Fact]
    public void EventsPerDay_KeepTwoDecimals()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.AverageDailyEpisodes.BelowRange = 0.03),
            Period(a => a.TimeInRange.AverageDailyEpisodes.BelowRange = 0.04));

        var events = Metric(comparison, PeriodComparisonMetric.HypoEventsPerDay);
        events.ValueA.Should().Be(0.03);
        events.ValueB.Should().Be(0.04);
        events.Delta.Should().Be(0.01);
    }

    [Fact]
    public void IndicesAndCountsKeepTheirDigits()
    {
        var comparison = Compare(
            Period(a =>
            {
                a.GRI.Score = 40.4;
                a.GlycemicVariability!.LowBloodGlucoseIndex = 1.04;
                a.DataSufficiency.DaysWithData = 12;
                a.DataSufficiency.AverageReadingsPerDay = 280.4;
            }),
            Period(a =>
            {
                a.GRI.Score = 41.0;
                a.GlycemicVariability!.LowBloodGlucoseIndex = 1.16;
                a.DataSufficiency.DaysWithData = 13;
                a.DataSufficiency.AverageReadingsPerDay = 281.2;
            }));

        Metric(comparison, PeriodComparisonMetric.Gri).Delta.Should().Be(1);
        Metric(comparison, PeriodComparisonMetric.Gri).ValueA.Should().Be(40);
        Metric(comparison, PeriodComparisonMetric.Lbgi).Delta.Should().Be(0.1);
        Metric(comparison, PeriodComparisonMetric.DaysWithData).Delta.Should().Be(1);
        Metric(comparison, PeriodComparisonMetric.ReadingsPerDay).Delta.Should().Be(1);
    }

    [Fact]
    public void InsulinIsRoundedToOneDecimalCarbsToWholeGramsAndIcrToTwoDecimals()
    {
        var comparison = Compare(
            Period(insulin: i => { i.Tdd = 44.24; i.TotalBasalPerDay = 20.04; i.CarbsPerDay = 150.4; i.IcRatio = 11.04; }),
            Period(insulin: i => { i.Tdd = 46.86; i.TotalBasalPerDay = 20.31; i.CarbsPerDay = 151.0; i.IcRatio = 11.35; }));

        var insulin = Metric(comparison, PeriodComparisonMetric.InsulinPerDay);
        insulin.ValueA.Should().Be(44.2);
        insulin.ValueB.Should().Be(46.9);
        insulin.Delta.Should().Be(2.6);
        Metric(comparison, PeriodComparisonMetric.BasalPerDay).Delta.Should().Be(0.3);
        var carbs = Metric(comparison, PeriodComparisonMetric.CarbsPerDay);
        carbs.ValueA.Should().Be(150);
        carbs.ValueB.Should().Be(151);
        carbs.Delta.Should().Be(1);
        Metric(comparison, PeriodComparisonMetric.Icr).Delta.Should().Be(0.31);
    }

    [Fact]
    public void AChangeThatRoundsToNothing_IsZero_NotNegativeZero()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.Percentages.Target = 61.04),
            Period(a => a.TimeInRange.Percentages.Target = 61.0));

        var delta = Metric(comparison, PeriodComparisonMetric.TirTarget).Delta;
        delta.Should().Be(0);
        double.IsNegative(delta!.Value).Should().BeFalse();
    }

    [Fact]
    public void AZeroBaseline_HasNoPercentChangeWhateverTheChange()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.Percentages.VeryLow = 0),
            Period(a => a.TimeInRange.Percentages.VeryLow = 2));

        var veryLow = Metric(comparison, PeriodComparisonMetric.VeryLowPercent);
        veryLow.Delta.Should().Be(2);
        veryLow.PercentChange.Should().BeNull();
    }

    [Fact]
    public void TwoZeroes_HaveAZeroChangeAndNoPercentChange()
    {
        var comparison = Compare(Period(), Period());

        var veryLow = Metric(comparison, PeriodComparisonMetric.VeryLowPercent);
        veryLow.Delta.Should().Be(0);
        veryLow.PercentChange.Should().BeNull();
    }

    [Fact]
    public void PercentChange_IsRelativeToTheBaselinesMagnitude()
    {
        var comparison = Compare(
            Period(a => a.TimeInRange.Percentages.Target = 80),
            Period(a => a.TimeInRange.Percentages.Target = 60));

        Metric(comparison, PeriodComparisonMetric.TirTarget).PercentChange.Should().BeApproximately(-25, 1e-9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APeriodWithNoReadings_HasNoData_AndEveryMetricOfItIsNull(bool first)
    {
        var empty = Period(a => a.TimeInRange.Percentages.Target = 0, readings: 0);
        var measured = Period(a => a.TimeInRange.Percentages.Target = 70);

        var comparison = first ? Compare(empty, measured) : Compare(measured, empty);

        var (noData, data) = first ? (comparison.PeriodA, comparison.PeriodB) : (comparison.PeriodB, comparison.PeriodA);
        noData.HasData.Should().BeFalse();
        noData.TimeInRange.Should().BeNull();
        data.HasData.Should().BeTrue();
        data.TimeInRange!.Target.Should().Be(70);
        comparison.Metrics.Should().OnlyContain(m => m.Delta == null && m.PercentChange == null);
        var tir = Metric(comparison, PeriodComparisonMetric.TirTarget);
        (first ? tir.ValueA : tir.ValueB).Should().BeNull();
        (first ? tir.ValueB : tir.ValueA).Should().Be(70);
    }

    [Fact]
    public void APeriodWithNoInsulinRecords_HasNoInsulinFigures()
    {
        var comparison = Compare(
            Period(insulin: i => { i.InsulinEventCount = 0; i.Tdd = 0; i.TotalBasalPerDay = 0; i.BasalPercent = 0; i.BolusesPerDay = 0; }),
            Period(insulin: i => i.Tdd = 46.9));

        var insulin = Metric(comparison, PeriodComparisonMetric.InsulinPerDay);
        insulin.ValueA.Should().BeNull();
        insulin.ValueB.Should().Be(46.9);
        insulin.Delta.Should().BeNull();
        Metric(comparison, PeriodComparisonMetric.BasalPerDay).ValueA.Should().BeNull();
        Metric(comparison, PeriodComparisonMetric.BasalPercent).ValueA.Should().BeNull();
        Metric(comparison, PeriodComparisonMetric.BolusesPerDay).ValueA.Should().BeNull();
    }

    [Fact]
    public void Icr_IsServedAtTwoDecimalsSoAUnitConversionOnTheClientKeepsItsDisplayedDigits()
    {
        var comparison = Compare(
            Period(insulin: i => i.IcRatio = 7.04),
            Period(insulin: i => i.IcRatio = 7.1449));

        var icr = Metric(comparison, PeriodComparisonMetric.Icr);
        icr.ValueA.Should().Be(7.04);
        icr.ValueB.Should().Be(7.14);
        icr.Delta.Should().Be(0.10);
        icr.ValueDecimals.Should().Be(2);
        icr.DeltaDecimals.Should().Be(2);
    }

    [Fact]
    public void APeriodWithNoCarbEntries_HasNoCarbFigure_AndNoIcrWhenNoBolusIsPairedWithAMeal()
    {
        var comparison = Compare(
            Period(insulin: i => { i.CarbCount = 0; i.CarbsPerDay = 0; i.IcRatio = null; }),
            Period(insulin: i => { i.CarbsPerDay = 150; i.IcRatio = 11.5; }));

        var carbs = Metric(comparison, PeriodComparisonMetric.CarbsPerDay);
        carbs.ValueA.Should().BeNull();
        carbs.ValueB.Should().Be(150);
        carbs.Delta.Should().BeNull();
        var icr = Metric(comparison, PeriodComparisonMetric.Icr);
        icr.ValueA.Should().BeNull();
        icr.Delta.Should().BeNull();
    }

    [Fact]
    public void Glucose_IsServedInMgDlAtWholeNumbers()
    {
        var comparison = Compare(
            Period(a => a.BasicStats.Mean = 100.4),
            Period(a => a.BasicStats.Mean = 154.4),
            GlucoseUnit.MgDl);

        comparison.Units.Should().Be(GlucoseUnit.MgDl);
        var mean = Metric(comparison, PeriodComparisonMetric.Mean);
        mean.ValueA.Should().Be(100);
        mean.ValueB.Should().Be(154);
        mean.Delta.Should().Be(54);
        mean.ValueDecimals.Should().Be(0);
        mean.DeltaDecimals.Should().Be(0);
    }

    [Fact]
    public void Glucose_IsServedInMmolAtOneDecimal_ConvertedBeforeTheChangeIsRounded()
    {
        var comparison = Compare(
            Period(a => a.BasicStats.Mean = 100),
            Period(a => a.BasicStats.Mean = 154),
            GlucoseUnit.Mmol);

        comparison.Units.Should().Be(GlucoseUnit.Mmol);
        var mean = Metric(comparison, PeriodComparisonMetric.Mean);
        mean.ValueA.Should().Be(5.5);
        mean.ValueB.Should().Be(8.5);
        mean.Delta.Should().Be(3.0);
        mean.ValueDecimals.Should().Be(1);
        mean.DeltaDecimals.Should().Be(1);
    }

    [Fact]
    public void AOneMgDlGlucoseChange_IsAVisibleChangeInMgDl_AndStillOneTenthInMmol()
    {
        var a = Period(x => x.BasicStats.Median = 100);
        var b = Period(x => x.BasicStats.Median = 101);

        Metric(Compare(a, b, GlucoseUnit.MgDl), PeriodComparisonMetric.Median).Delta.Should().Be(1);
        Metric(Compare(a, b, GlucoseUnit.Mmol), PeriodComparisonMetric.Median).Delta.Should().Be(0.1);
    }

    [Fact]
    public void AGlucoseChangeUnderHalfAMgDl_IsNoChangeInMgDl()
    {
        var a = Period(x => x.GlycemicVariability!.StandardDeviation = 40.0);
        var b = Period(x => x.GlycemicVariability!.StandardDeviation = 40.4);

        var sd = Metric(Compare(a, b, GlucoseUnit.MgDl), PeriodComparisonMetric.Sd);
        sd.Delta.Should().Be(0);
        sd.PercentChange.Should().BeApproximately(1.0, 1e-9);
    }

    private static readonly PeriodComparisonMetric[] GlucoseMetrics =
    [
        PeriodComparisonMetric.Mean,
        PeriodComparisonMetric.Median,
        PeriodComparisonMetric.Sd,
        PeriodComparisonMetric.Mage,
        PeriodComparisonMetric.Conga,
    ];

    private static PeriodComparisonCalculator.PeriodFigures Filled(double value) =>
        Period(
            a =>
            {
                a.TimeInRange.Percentages.Target = value;
                a.TimeInRange.Percentages.VeryLow = value;
                a.TimeInRange.Percentages.Low = value;
                a.TimeInRange.Percentages.High = value;
                a.TimeInRange.Percentages.VeryHigh = value;
                a.TimeInRange.AverageDailyMinutes.High = value;
                a.TimeInRange.AverageDailyMinutes.Low = value;
                a.TimeInRange.AverageDailyEpisodes.AboveRange = value;
                a.TimeInRange.AverageDailyEpisodes.BelowRange = value;
                a.GMI.Value = value;
                a.GRI.Score = value;
                a.BasicStats.Mean = value;
                a.BasicStats.Median = value;
                a.GlycemicVariability!.CoefficientOfVariation = value;
                a.GlycemicVariability.StandardDeviation = value;
                a.GlycemicVariability.LowBloodGlucoseIndex = value;
                a.GlycemicVariability.HighBloodGlucoseIndex = value;
                a.GlycemicVariability.MeanAmplitudeGlycemicExcursions = value;
                a.GlycemicVariability.ContinuousOverlappingNetGlycemicAction = value;
                a.GlycemicVariability.AverageDailyRiskRange = value;
                a.DataQuality.CgmActivePercent = value;
                a.DataSufficiency.DaysWithData = (int)value;
                a.DataSufficiency.AverageReadingsPerDay = value;
            },
            i =>
            {
                i.Tdd = value;
                i.TotalBasalPerDay = value;
                i.BasalPercent = value;
                i.BolusesPerDay = value;
                i.CarbsPerDay = value;
                i.IcRatio = value;
            },
            new PersonalRangeTimeInRange { InRangePercent = value });

    public static TheoryData<PeriodComparisonMetric> GlucoseMetricCases() => new(GlucoseMetrics);

    [Theory]
    [MemberData(nameof(GlucoseMetricCases))]
    public void EveryGlucoseMetric_IsConvertedToMmolBeforeItIsRounded(PeriodComparisonMetric metric)
    {
        var comparison = Compare(Filled(90), Filled(180), GlucoseUnit.Mmol);

        var served = Metric(comparison, metric);
        served.ValueA.Should().Be(5.0);
        served.ValueB.Should().Be(10.0);
        served.Delta.Should().Be(5.0);
        served.ValueDecimals.Should().Be(1);
        served.DeltaDecimals.Should().Be(1);
    }

    [Fact]
    public void EveryOtherMetric_IsServedAsItIsWhateverTheGlucoseUnits()
    {
        var inMgDl = Compare(Filled(90), Filled(180), GlucoseUnit.MgDl);
        var inMmol = Compare(Filled(90), Filled(180), GlucoseUnit.Mmol);

        var others = inMgDl.Metrics.Where(m => !GlucoseMetrics.Contains(m.Metric)).ToList();
        others.Should().HaveCount(Enum.GetValues<PeriodComparisonMetric>().Length - GlucoseMetrics.Length);
        foreach (var expected in others)
        {
            var served = Metric(inMmol, expected.Metric);
            served.ValueA.Should().NotBeNull(expected.Metric.ToString());
            served.Should().BeEquivalentTo(expected, expected.Metric.ToString());
        }
    }

    [Fact]
    public void ThePersonalRange_IsLeftOutWhenNeitherPeriodHasOne()
    {
        var comparison = Compare(Period(), Period());

        comparison.Metrics.Should().NotContain(m => m.Metric == PeriodComparisonMetric.PersonalRangePercent);
    }

    [Fact]
    public void ThePersonalRange_IsServedWhenEitherPeriodHasOne()
    {
        var comparison = Compare(
            Period(),
            Period(personalRange: new PersonalRangeTimeInRange { InRangePercent = 61.5 }));

        var range = Metric(comparison, PeriodComparisonMetric.PersonalRangePercent);
        range.ValueA.Should().BeNull();
        range.ValueB.Should().Be(61.5);
        range.Delta.Should().BeNull();
    }

    [Fact]
    public void EveryMetricIsServedOnce_InAnyComparison()
    {
        var comparison = Compare(
            Period(personalRange: new PersonalRangeTimeInRange { InRangePercent = 50 }),
            Period(personalRange: new PersonalRangeTimeInRange { InRangePercent = 55 }));

        comparison.Metrics.Select(m => m.Metric).Should().BeEquivalentTo(Enum.GetValues<PeriodComparisonMetric>());
        comparison.Metrics.Should().OnlyHaveUniqueItems(m => m.Metric);
    }

    [Fact]
    public void TheTimeZoneReason_OfEitherPeriodIsCarried()
    {
        var withReason = Period();
        withReason.Analysis.TimeZoneUnavailableReason = TimeZoneUnavailableReason.NotConfigured;

        Compare(Period(), withReason).TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
        Compare(withReason, Period()).TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.NotConfigured);
        Compare(Period(), Period()).TimeZoneUnavailableReason.Should().BeNull();
    }
}
