using Nocturne.Core.Clinical.Statistics;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class IntervalUnionTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (DateTime Start, DateTime End) Minutes(double from, double to) =>
        (T0.AddMinutes(from), T0.AddMinutes(to));

    [Fact]
    public void Merge_OfNothing_IsEmpty()
    {
        IntervalUnion.Merge([]).Should().BeEmpty();
        IntervalUnion.Length([]).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Merge_AdjacentHalfOpenIntervals_JoinIntoOne()
    {
        IntervalUnion.Merge([Minutes(5, 10), Minutes(0, 5)])
            .Should().Equal(Minutes(0, 10));
    }

    [Fact]
    public void Merge_OverlappingIntervals_CoverTheirSharedTimeOnce()
    {
        IntervalUnion.Merge([Minutes(0, 30), Minutes(20, 40)])
            .Should().Equal(Minutes(0, 40));
        IntervalUnion.Length([Minutes(0, 30), Minutes(20, 40)])
            .Should().Be(TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void Merge_NestedInterval_AddsNothing()
    {
        IntervalUnion.Merge([Minutes(10, 20), Minutes(0, 60)])
            .Should().Equal(Minutes(0, 60));
    }

    [Fact]
    public void Merge_DisjointIntervals_StaySeparateInTimeOrder()
    {
        IntervalUnion.Merge([Minutes(30, 40), Minutes(0, 10)])
            .Should().Equal(Minutes(0, 10), Minutes(30, 40));
        IntervalUnion.Length([Minutes(30, 40), Minutes(0, 10)])
            .Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Merge_EmptyAndReversedIntervals_CoverNothing()
    {
        IntervalUnion.Merge([Minutes(5, 5), Minutes(20, 10), Minutes(30, 35)])
            .Should().Equal(Minutes(30, 35));
    }
}
