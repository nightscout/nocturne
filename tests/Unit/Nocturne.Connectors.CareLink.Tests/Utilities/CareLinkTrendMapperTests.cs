using FluentAssertions;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Utilities;

public class CareLinkTrendMapperTests
{
    [Theory]
    [InlineData("UP", GlucoseDirection.SingleUp)]
    [InlineData("UP_DOUBLE", GlucoseDirection.DoubleUp)]
    [InlineData("UP_TRIPLE", GlucoseDirection.DoubleUp)]
    [InlineData("DOWN", GlucoseDirection.SingleDown)]
    [InlineData("DOWN_DOUBLE", GlucoseDirection.DoubleDown)]
    [InlineData("DOWN_TRIPLE", GlucoseDirection.DoubleDown)]
    [InlineData("NONE", GlucoseDirection.None)]
    public void Map_ReturnsExpectedDirection(string trend, GlucoseDirection expected)
    {
        CareLinkTrendMapper.Map(trend).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    public void Map_ReturnsNone_ForUnknownTrend(string? trend)
    {
        CareLinkTrendMapper.Map(trend).Should().Be(GlucoseDirection.None);
    }
}
