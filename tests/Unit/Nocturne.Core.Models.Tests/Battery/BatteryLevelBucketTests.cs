using FluentAssertions;
using Nocturne.Core.Models.Battery;
using Xunit;

namespace Nocturne.Core.Models.Tests.Battery;

[Trait("Category", "Unit")]
public class BatteryLevelBucketTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 25)]
    [InlineData(29, 25)]
    [InlineData(30, 50)]
    [InlineData(54, 50)]
    [InlineData(55, 75)]
    [InlineData(94, 75)]
    [InlineData(95, 100)]
    [InlineData(100, 100)]
    public void Of_BucketsPercent(int? percent, int expected)
    {
        BatteryLevelBucket.Of(percent).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(29, 25)]
    [InlineData(95, 100)]
    public void ReadingAndStatistics_AgreeOnLevel(int? percent, int expected)
    {
        new BatteryReading { Battery = percent }.Level.Should().Be(expected);
        new BatteryStatistics { CurrentLevel = percent }.Level.Should().Be(expected);
    }
}
