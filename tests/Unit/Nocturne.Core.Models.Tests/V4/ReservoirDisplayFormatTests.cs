using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class ReservoirDisplayFormatTests
{
    [Theory]
    [InlineData("50+U", true)]
    [InlineData("50+ U", true)]
    [InlineData("50 + U", true)]
    [InlineData(" 50.0+U", true)]
    [InlineData("50+", true)]
    [InlineData("42.5U", false)]
    [InlineData("Low", false)]
    [InlineData("+50U", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLowerBound(string? display, bool expected)
    {
        ReservoirDisplayFormat.IsLowerBound(display).Should().Be(expected);
    }
}
