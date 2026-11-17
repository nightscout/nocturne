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
    [InlineData(0.015, "0.01")]
    [InlineData(2.205, "2.21")]
    [InlineData(0.0, "0")]
    public void Display_FollowsNightscoutsToFixed(double iob, string expected)
    {
        var result = new IobResult { Iob = iob };

        Assert.Equal(expected, result.Display);
        Assert.Equal($"IOB: {expected}U", result.DisplayLine);
    }
}

public class JsNumberToFixed2Tests
{
    [Theory]
    [InlineData(0.015, "0.01")]
    [InlineData(0.045, "0.04")]
    [InlineData(0.105, "0.10")]
    [InlineData(0.125, "0.13")]
    [InlineData(19.625, "19.63")]
    [InlineData(2.205, "2.21")]
    [InlineData(1.005, "1.00")]
    [InlineData(1.255, "1.25")]
    [InlineData(0.995, "0.99")]
    [InlineData(0.005, "0.01")]
    [InlineData(-0.015, "-0.01")]
    [InlineData(-0.125, "-0.13")]
    [InlineData(-2.205, "-2.21")]
    [InlineData(-1.005, "-1.00")]
    [InlineData(-0.001, "-0.00")]
    [InlineData(0.0, "0.00")]
    [InlineData(1.0, "1.00")]
    [InlineData(10.0, "10.00")]
    public void ToFixed2_MatchesJavaScript(double value, string expected)
    {
        Assert.Equal(expected, JsNumber.ToFixed2(value));
    }
}
