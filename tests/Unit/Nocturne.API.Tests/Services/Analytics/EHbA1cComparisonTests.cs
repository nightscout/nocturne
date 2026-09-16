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
        point.Unweighted14DayPercent.Should().Be(8.6);
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
        var unweighted14Mean = WeightedMean(sums, end, 14, _ => 1);

        point.EstimatedA1cPercent.Should().Be(Adag(currentMean));
        point.Linear90DayPercent.Should().Be(Adag(linearMean));
        point.HalfLife30DayPercent.Should().Be(Adag(halfLifeMean));
        point.Unweighted90DayPercent.Should().Be(Adag(unweightedMean));
        point.Weighted120DayPercent.Should().Be(Adag(weighted120Mean));
        point.Unweighted14DayPercent.Should().Be(Adag(unweighted14Mean));
    }

    [Fact]
    public void ShortWindow_CrossesLongWindowAfterTrendReversal()
    {
        var rising = Enumerable.Repeat(100.0, 107)
            .Concat(Enumerable.Repeat(250.0, 14))
            .ToArray();
        var falling = Enumerable.Repeat(250.0, 107)
            .Concat(Enumerable.Repeat(100.0, 14))
            .ToArray();
        var counts = Enumerable.Repeat(1, 121).ToArray();

        var risingPoint = DataOverviewService.BuildEHbA1cPoints(
            rising, counts, RangeStart, 90, 2024, PointDate).Single();
        var fallingPoint = DataOverviewService.BuildEHbA1cPoints(
            falling, counts, RangeStart, 90, 2024, PointDate).Single();

        risingPoint.Unweighted14DayPercent.Should().NotBeNull();
        risingPoint.Unweighted90DayPercent.Should().NotBeNull();
        fallingPoint.Unweighted14DayPercent.Should().NotBeNull();
        fallingPoint.Unweighted90DayPercent.Should().NotBeNull();
        risingPoint.Unweighted14DayPercent!.Value.Should()
            .BeGreaterThan(risingPoint.Unweighted90DayPercent!.Value);
        fallingPoint.Unweighted14DayPercent!.Value.Should()
            .BeLessThan(fallingPoint.Unweighted90DayPercent!.Value);
    }

    [Fact]
    public void SparseRecentData_DoesNotEmitAnEstimate()
    {
        var sums = new double[121];
        var counts = new int[121];
        for (var i = 92; i < sums.Length; i++)
        {
            sums[i] = 250;
            counts[i] = 1;
        }

        var points = DataOverviewService.BuildEHbA1cPoints(
            sums, counts, RangeStart, 90, 2024, PointDate);

        points.Should().BeEmpty();
    }

    [Fact]
    public void ThirtyDaysWithData_EmitsAnEstimate()
    {
        var sums = new double[121];
        var counts = new int[121];
        for (var i = 91; i < sums.Length; i++)
        {
            sums[i] = 250;
            counts[i] = 1;
        }

        var points = DataOverviewService.BuildEHbA1cPoints(
            sums, counts, RangeStart, 90, 2024, PointDate);

        points.Should().ContainSingle();
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
