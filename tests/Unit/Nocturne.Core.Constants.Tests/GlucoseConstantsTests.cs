using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

public class GlucoseConstantsTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(180)]
    [InlineData(400)]
    public void ConvertingToMmolAndBack_ReturnsTheOriginalMgdl(double mgdl)
    {
        var mmol = mgdl / GlucoseConstants.MgdlPerMmol;
        var roundTripped = mmol * GlucoseConstants.MgdlPerMmol;

        roundTripped.Should().BeApproximately(mgdl, 1e-9);
    }

    [Theory]
    [InlineData(5.5, 99)]
    [InlineData(4.0, 72)]
    [InlineData(10.0, 180)]
    public void MmolConvertsToTheExpectedRoundedMgdl(double mmol, double expectedMgdl)
    {
        Math.Round(mmol * GlucoseConstants.MgdlPerMmol).Should().Be(expectedMgdl);
    }

    [Theory]
    [InlineData(72, 4.0)]
    [InlineData(180, 10.0)]
    [InlineData(400, 22.2)]
    public void MgdlConvertsToTheExpectedRoundedMmol(double mgdl, double expectedMmol)
    {
        Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1).Should().Be(expectedMmol);
    }

    /// <summary>
    /// 100 mg/dL sits either side of the 5.55 mmol/L rounding boundary depending on which of the two
    /// factors historically present in the tree is used, so it pins which one survived.
    /// </summary>
    [Fact]
    public void OneHundredMgdlDisplaysAsFivePointFiveMmol()
    {
        Math.Round(100 / GlucoseConstants.MgdlPerMmol, 1).Should().Be(5.5);
        Math.Round(100 / 18.01559, 1).Should().Be(5.6);
    }
}
