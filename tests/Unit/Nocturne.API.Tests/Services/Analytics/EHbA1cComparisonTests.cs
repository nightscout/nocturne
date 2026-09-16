using FluentAssertions;
using Nocturne.API.Services.Analytics;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class EHbA1cComparisonTests
{
    private static readonly DateTime RangeStart = new(2023, 9, 3);
    private static readonly DateTime PointDate = new(2024, 1, 1);

    [Fact]
    public void ConstantGlucose_PinsAdagAndGmiFormulas()
    {
        var sums = Enumerable.Repeat(200.0, 121).ToArray();
        var counts = Enumerable.Repeat(1, 121).ToArray();

        var point = DataOverviewService.BuildEHbA1cPoints(
            sums, counts, RangeStart, 90, 2024, PointDate).Single();

        point.EstimatedA1cPercent.Should().Be(8.6);
        point.Linear90DayPercent.Should().Be(8.6);
        point.HalfLife30DayPercent.Should().Be(8.6);
        point.Unweighted90DayPercent.Should().Be(8.6);
        point.Weighted120DayPercent.Should().Be(8.6);
        point.Gmi14DayPercent.Should().Be(8.09);
    }

    [Fact]
    public void ChangingGlucose_PinsEveryWeightingMethodIndependently()
    {
        var sums = Enumerable.Range(0, 121).Select(day => 80.0 + day).ToArray();
        var counts = Enumerable.Repeat(1, 121).ToArray();

        var point = DataOverviewService.BuildEHbA1cPoints(
            sums, counts, RangeStart, 90, 2024, PointDate).Single();
        var end = sums.Length - 1;

        var currentDecay = Math.Pow((Math.Sqrt(5) - 1) / 2, 1.0 / 30.0);
        var halfLifeDecay = Math.Pow(0.5, 1.0 / 30.0);
        var currentMean = WeightedMean(sums, end, 90, age => Math.Pow(currentDecay, age));
        var linearMean = WeightedMean(sums, end, 90, age => 90 - age);
        var halfLifeMean = WeightedMean(sums, end, 90, age => Math.Pow(halfLifeDecay, age));
        var unweightedMean = WeightedMean(sums, end, 90, _ => 1);
        var weighted120Mean = WeightedMean(sums, end, 120, age => age < 30 ? 4 : age < 60 ? 2 : 1);
        var gmiMean = WeightedMean(sums, end, 14, _ => 1);

        point.EstimatedA1cPercent.Should().Be(Adag(currentMean));
        point.Linear90DayPercent.Should().Be(Adag(linearMean));
        point.HalfLife30DayPercent.Should().Be(Adag(halfLifeMean));
        point.Unweighted90DayPercent.Should().Be(Adag(unweightedMean));
        point.Weighted120DayPercent.Should().Be(Adag(weighted120Mean));
        point.Gmi14DayPercent.Should().Be(Math.Round(3.31 + 0.02392 * gmiMean, 2));
    }

    private static double WeightedMean(
        IReadOnlyList<double> values,
        int end,
        int days,
        Func<int, double> weightForAge)
    {
        var weightedSum = 0.0;
        var totalWeight = 0.0;
        for (var age = 0; age < days; age++)
        {
            var weight = weightForAge(age);
            weightedSum += values[end - age] * weight;
            totalWeight += weight;
        }

        return weightedSum / totalWeight;
    }

    private static double Adag(double meanMgdl) => Math.Round((meanMgdl + 46.7) / 28.7, 2);
}
