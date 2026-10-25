using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class IobResultDisplayTests
{
    [Theory]
    [InlineData(2.0, "2.00")]
    [InlineData(1.125, "1.13")]
    [InlineData(-0.022, "-0.02")]
    [InlineData(-1.333, "-1.33")]
    [InlineData(-0.001, "0.00")]
    [InlineData(0.0, "0")]
    public void FormatDisplay_FollowsNightscoutsToFixed(double iob, string expected)
    {
        Assert.Equal(expected, IobResult.FormatDisplay(iob));
    }
}
