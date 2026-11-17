using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Clinical.Tests.Statistics;

/// <summary>
/// A population's targets are percentages of time in a range the population also defines. The
/// pregnancy consensus (Battelino et al., Diabetes Care 2019;42:1593) measures time in range over
/// 63-140 mg/dL, not 70-180, and grades it against at least 70% in range and at most 25% above
/// it, so grading those percentages against time measured over 70-180 passes a pregnancy that
/// spends half its time above 140.
/// </summary>
public class PopulationTargetRangeTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly StatisticsService _sut = new(TimeProvider.System);

    /// <summary>
    /// A day alternating between 120 mg/dL (inside 63-140) and 160 mg/dL (above 140, inside
    /// 70-180), five minutes apart: half the day in the pregnancy range, all of it in the
    /// general one.
    /// </summary>
    private static List<SensorGlucose> HalfAbovePregnancyRange() =>
        Enumerable.Range(0, 288)
            .Select(i => new SensorGlucose
            {
                Mgdl = i % 2 == 0 ? 120 : 160,
                Timestamp = Start.AddMinutes(i * 5),
            })
            .ToList();

    private ExtendedGlucoseAnalytics Analyze(DiabetesPopulation population) =>
        _sut.AnalyzeGlucoseDataExtended(HalfAbovePregnancyRange(), [], [], windowDays: 1, population);

    [Theory]
    [InlineData(DiabetesPopulation.Pregnancy)]
    [InlineData(DiabetesPopulation.PregnancyType1)]
    public void AnalyzeGlucoseDataExtended_Pregnancy_GradesTimeInRangeMeasuredOverThePregnancyRange(
        DiabetesPopulation population)
    {
        var assessment = Analyze(population).ClinicalAssessment;

        assessment.Targets.TargetLow.Should().Be(63);
        assessment.Targets.TargetHigh.Should().Be(140);
        assessment.TIRAssessment.CurrentValue.Should().BeApproximately(50, 0.5,
            "half the readings are above 140 mg/dL, outside the pregnancy range");
        assessment.TIRAssessment.Status.Should().Be(TargetStatus.NotMet,
            "50% in range misses the pregnancy target of 70%");
    }

    [Theory]
    [InlineData(DiabetesPopulation.Pregnancy)]
    [InlineData(DiabetesPopulation.PregnancyType1)]
    public void AnalyzeGlucoseDataExtended_Pregnancy_GradesTimeAboveRangeMeasuredOverThePregnancyRange(
        DiabetesPopulation population)
    {
        var assessment = Analyze(population).ClinicalAssessment;

        assessment.TARAssessment.CurrentValue.Should().BeApproximately(50, 0.5);
        assessment.TARAssessment.Status.Should().Be(TargetStatus.NotMet,
            "50% above range misses the pregnancy ceiling of 25%");
    }

    /// <summary>
    /// The same day graded for a general adult population, over 70-180: the control that shows
    /// the population alone is what should move the two assessments above.
    /// </summary>
    [Fact]
    public void AnalyzeGlucoseDataExtended_Type1Adult_GradesTheSameDayOverTheGeneralRange()
    {
        var assessment = Analyze(DiabetesPopulation.Type1Adult).ClinicalAssessment;

        assessment.TIRAssessment.CurrentValue.Should().BeApproximately(100, 0.5);
        assessment.TIRAssessment.Status.Should().Be(TargetStatus.Met);
        assessment.TARAssessment.CurrentValue.Should().BeApproximately(0, 0.5);
    }

    /// <summary>
    /// A day at 65 mg/dL: inside the pregnancy range (63-140) and below the general one (70-180).
    /// </summary>
    private ExtendedGlucoseAnalytics AnalyzeAtSixtyFive(DiabetesPopulation population) =>
        _sut.AnalyzeGlucoseDataExtended(
            Enumerable.Range(0, 288)
                .Select(i => new SensorGlucose { Mgdl = 65, Timestamp = Start.AddMinutes(i * 5) })
                .ToList(),
            [],
            [],
            windowDays: 1,
            population);

    [Theory]
    [InlineData(DiabetesPopulation.Type1Adult, 70, 180, 0, 100)]
    [InlineData(DiabetesPopulation.Pregnancy, 63, 140, 100, 0)]
    [InlineData(DiabetesPopulation.PregnancyType1, 63, 140, 100, 0)]
    public void AnalyzeGlucoseDataExtended_ServesTheEdgesItMeasuredTimeInRangeAgainst(
        DiabetesPopulation population,
        double low,
        double high,
        double expectedTarget,
        double expectedLow)
    {
        var analysis = AnalyzeAtSixtyFive(population);
        var served = analysis.TimeInRange.Thresholds;

        served.Should().NotBeNull();
        served!.TargetBottom.Should().Be(low);
        served.Low.Should().Be(low);
        served.TargetTop.Should().Be(high);
        served.High.Should().Be(high);
        served.VeryLow.Should().Be(54);
        served.VeryHigh.Should().Be(250);
        analysis.ClinicalAssessment.Targets.TargetLow.Should().Be(served.TargetBottom);
        analysis.ClinicalAssessment.Targets.TargetHigh.Should().Be(served.TargetTop);
        analysis.TimeInRange.Percentages.Target.Should().BeApproximately(expectedTarget, 0.5);
        analysis.TimeInRange.Percentages.Low.Should().BeApproximately(expectedLow, 0.5);
    }

    [Theory]
    [InlineData(DiabetesPopulation.Type1Adult, 70, 180)]
    [InlineData(DiabetesPopulation.Pregnancy, 63, 140)]
    public void AnalyzeGlucoseDataExtended_WithNoReadings_StillServesThePopulationsEdges(
        DiabetesPopulation population,
        double low,
        double high)
    {
        var served = _sut
            .AnalyzeGlucoseDataExtended([], [], [], windowDays: 1, population)
            .TimeInRange
            .Thresholds;

        served.Should().NotBeNull();
        served!.TargetBottom.Should().Be(low);
        served.TargetTop.Should().Be(high);
    }
}
