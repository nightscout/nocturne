using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Data too sparse or too degenerate for a statistic yields an explicit absence — null or
/// <see cref="TargetStatus.NotAssessed"/> — never NaN, and never a target reported as met.
/// </summary>
public class StatisticsServiceInsufficientDataTests
{
    private readonly StatisticsService _service = new();

    [Fact]
    public void GlycemicVariability_AllZeroValues_IsNullRatherThanNaN()
    {
        _service.CalculateGlycemicVariability([0, 0], []).Should().BeNull();
    }

    [Fact]
    public void GlycemicVariability_ImplausibleValuesAmongReadings_AreDroppedAndEveryMetricIsFinite()
    {
        var result = _service.CalculateGlycemicVariability([0, -5, 100, 140, 180], []);

        result.Should().NotBeNull();
        typeof(GlycemicVariability)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(double))
            .Select(p => (Name: p.Name, Value: (double)p.GetValue(result)!))
            .Should()
            .OnlyContain(metric => double.IsFinite(metric.Value));
        result!.CoefficientOfVariation.Should().Be(28.6);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    public void Lbgi_ReadingBelowTransformDomain_CountsAsMaximumLowRisk(double mgdl)
    {
        var lbgi = _service.CalculateLBGI([mgdl, 120]);

        double.IsFinite(lbgi).Should().BeTrue();
        lbgi.Should().Be(_service.CalculateLBGI([18, 120]));
        lbgi.Should().BeGreaterThan(_service.CalculateLBGI([20, 120]));
    }

    [Fact]
    public void GlycemicVariability_ImplausibleEntry_IsExcludedFromTimeBasedMetrics()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SensorGlucose At(int minutes, double mgdl) =>
            new() { Mgdl = mgdl, Timestamp = start.AddMinutes(minutes) };
        double[] values = [100, 140, 180];
        SensorGlucose[] plausible = [At(0, 100), At(10, 140), At(15, 180)];

        var withImplausible = _service.CalculateGlycemicVariability(
            values, [At(0, 100), At(5, 700), At(10, 140), At(15, 180)]);
        var withoutImplausible = _service.CalculateGlycemicVariability(values, plausible);

        withImplausible.Should().BeEquivalentTo(withoutImplausible);
    }

    [Theory]
    [InlineData(VarianceMode.Population)]
    [InlineData(VarianceMode.Sample)]
    public void Variance_EmptySeries_IsZero(VarianceMode mode)
    {
        GlucoseStatistics.Variance([], 0, mode).Should().Be(0);
        GlucoseStatistics.StandardDeviation([], mode).Should().Be(0);
    }

    [Fact]
    public void AssessAgainstTargets_NoReadings_AssessesNothingAndGradesNothing()
    {
        var result = _service.AssessAgainstTargets(new GlucoseAnalytics());

        var assessments = new[]
        {
            result.TIRAssessment,
            result.TBRAssessment,
            result.VeryLowAssessment,
            result.TARAssessment,
            result.VeryHighAssessment,
            result.CVAssessment,
        };
        assessments.Should().OnlyContain(a =>
            a.Status == TargetStatus.NotAssessed && a.CurrentValue == null);
        result.TargetsMet.Should().Be(0);
        result.TargetsAssessed.Should().Be(0);
        result.OverallAssessment.Should().Be(ClinicalAssessmentLevel.InsufficientData);
        result.Strengths.Should().BeEmpty();
        result.PriorityAreas.Should().BeEmpty();
        result.ActionableInsights.Should().BeEmpty();
    }

    [Fact]
    public void AssessAgainstTargets_MissingVariability_CvTargetIsNotAssessedRatherThanMet()
    {
        var analytics = new GlucoseAnalytics
        {
            BasicStats = new BasicGlucoseStats { Count = 1 },
            TimeInRange = new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages { Target = 100 },
            },
            GlycemicVariability = null,
        };

        var result = _service.AssessAgainstTargets(analytics);

        result.CVAssessment.Status.Should().Be(TargetStatus.NotAssessed);
        result.CVAssessment.CurrentValue.Should().BeNull();
        result.TIRAssessment.Status.Should().Be(TargetStatus.Met);
        result.TargetsMet.Should().Be(5);
        result.TargetsAssessed.Should().Be(5);
        result.OverallAssessment.Should().Be(ClinicalAssessmentLevel.InsufficientData);
        result.Strengths.Should().NotContain(s => s.Key == InsightKey.VariabilityControlled);
        result.ActionableInsights.Should().NotContain(i => i.Key == InsightKey.AllTargetsAchieved);
    }
}
