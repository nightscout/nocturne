using Xunit;

namespace Nocturne.Core.Models.Tests;

public class OnBoardRoundingTests
{
    [Theory]
    [InlineData(1.234, 1.23)]
    [InlineData(1.236, 1.24)]
    [InlineData(0.125, 0.12)]
    [InlineData(0.375, 0.38)]
    [InlineData(0.175, 0.18)]
    [InlineData(-0.125, -0.12)]
    [InlineData(0.0, 0.0)]
    public void Iob_RoundsToTwoPlacesAndAMidpointToEven(double units, double expected)
    {
        Assert.Equal(expected, OnBoardRounding.Iob(units));
    }

    [Theory]
    [InlineData(12.4, 12.0)]
    [InlineData(12.6, 13.0)]
    [InlineData(2.5, 2.0)]
    [InlineData(3.5, 4.0)]
    [InlineData(0.0, 0.0)]
    public void Cob_RoundsToWholeGramsAndAMidpointToEven(double grams, double expected)
    {
        Assert.Equal(expected, OnBoardRounding.Cob(grams));
    }
}
