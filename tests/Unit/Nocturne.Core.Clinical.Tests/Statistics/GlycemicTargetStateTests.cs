using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class GlycemicTargetStateTests
{
    private readonly StatisticsService _service = new();

    [Theory]
    [InlineData(6.9, true, true)]
    [InlineData(6.94, true, true)]
    [InlineData(6.95, false, true)]
    [InlineData(6.9538, false, false)]
    [InlineData(7.0, false, false)]
    [InlineData(7.1, false, false)]
    public void EstimatedA1cMeetsTarget_IsJudgedPerUnitAtItsDisplayedPrecision(
        double estimatedA1c,
        bool percentMet,
        bool mmolMolMet)
    {
        var state = new GlycemicVariability { EstimatedA1c = estimatedA1c }.EstimatedA1cMeetsTarget;

        state.Percent.Should().Be(percentMet);
        state.MmolMol.Should().Be(mmolMolMet);
    }

    [Theory]
    [InlineData(35, TargetStatus.Met)]
    [InlineData(36, TargetStatus.Met)]
    [InlineData(36.4, TargetStatus.Met)]
    [InlineData(36.5, TargetStatus.Close)]
    [InlineData(37, TargetStatus.Close)]
    [InlineData(45, TargetStatus.NotMet)]
    public void CvAssessment_IsJudgedAtTheWholePercentItIsDisplayedAt(double cv, TargetStatus expected)
    {
        var analytics = new GlucoseAnalytics
        {
            BasicStats = new BasicGlucoseStats { Count = 100 },
            GlycemicVariability = new GlycemicVariability { CoefficientOfVariation = cv },
        };

        _service.AssessAgainstTargets(analytics).CVAssessment.Status.Should().Be(expected);
    }

    [Fact]
    public void CvAssessment_WithoutVariability_IsNotAssessed()
    {
        var analytics = new GlucoseAnalytics { BasicStats = new BasicGlucoseStats { Count = 100 } };

        _service.AssessAgainstTargets(analytics).CVAssessment.Status.Should().Be(TargetStatus.NotAssessed);
    }

    [Fact]
    public void CoefficientOfVariationTarget_IsTheTargetEveryPopulationAssessesAgainst()
    {
        var served = new GlycemicVariability().CoefficientOfVariationTarget;

        Enum.GetValues<DiabetesPopulation>()
            .Select(population => ClinicalTargets.ForPopulation(population).TargetCV)
            .Should().OnlyContain(target => target == served);
    }

    [Fact]
    public void CalculateGlycemicVariability_SteadyLowGlucose_MeetsTheA1cTarget()
    {
        var result = _service.CalculateGlycemicVariability([100d, 102d, 98d, 101d], [], TimeZoneInfo.Utc)!;

        result.EstimatedA1cMeetsTarget.Percent.Should().BeTrue();
    }

    [Fact]
    public void CalculateGlycemicVariability_HighGlucose_MissesTheA1cTarget()
    {
        var result = _service.CalculateGlycemicVariability([60d, 300d, 80d, 320d], [], TimeZoneInfo.Utc)!;

        result.EstimatedA1cMeetsTarget.Percent.Should().BeFalse();
    }

    [Fact]
    public void TimeInRangePercentages_BelowAndAboveRangeSumTheirBands()
    {
        var percentages = new TimeInRangePercentages { VeryLow = 1, Low = 3, High = 20, VeryHigh = 5 };

        percentages.BelowRange.Should().Be(4);
        percentages.AboveRange.Should().Be(25);
    }
}
