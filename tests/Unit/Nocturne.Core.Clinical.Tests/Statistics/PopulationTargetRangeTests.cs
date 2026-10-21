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

    private readonly StatisticsService _sut = new();

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
}
