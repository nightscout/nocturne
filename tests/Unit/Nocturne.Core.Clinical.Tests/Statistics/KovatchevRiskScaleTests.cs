using Nocturne.Core.Clinical.Statistics;

namespace Nocturne.Core.Clinical.Tests.Statistics;

/// <summary>
/// Pins LBGI and HBGI to the published Kovatchev risk scale, independently of the transform the
/// service implements.
/// </summary>
/// <remarks>
/// The transform (Kovatchev et al., Diabetes Care 1997;20:1655) has a mg/dL form,
/// <c>f = 1.509 * ((ln BG)^1.084 - 5.381)</c>, and a mmol/L form,
/// <c>f = 1.794 * ((ln BG)^1.026 - 1.861)</c>. Risk is <c>10 * f^2</c>. The expected values below come from the mg/dL form; the mmol/L form
/// agrees with every one of them to within 0.7%, so the 1% tolerance accepts either and nothing
/// else. The scale is constructed so that risk runs from 0 at about 112.5 mg/dL to 100 at the
/// ends of the 20-600 mg/dL range, which is what the published bands (LBGI above 5 is high risk,
/// HBGI above 9 is high risk) are calibrated against.
/// </remarks>
public class KovatchevRiskScaleTests
{
    private const double RelativeTolerance = 0.01;

    private readonly StatisticsService _sut = new(TimeProvider.System);

    [Theory]
    [InlineData(20.0, 100.042)]
    [InlineData(40.0, 36.418)]
    [InlineData(54.0, 18.461)]
    [InlineData(65.0, 10.351)]
    [InlineData(70.0, 7.755)]
    public void CalculateLBGI_SingleReading_MatchesThePublishedRiskScale(double mgdl, double expected)
    {
        _sut.CalculateLBGI([mgdl]).Should().BeApproximately(expected, expected * RelativeTolerance);
    }

    [Theory]
    [InlineData(180.0, 7.729)]
    [InlineData(250.0, 22.436)]
    [InlineData(300.0, 33.952)]
    [InlineData(600.0, 99.948)]
    public void CalculateHBGI_SingleReading_MatchesThePublishedRiskScale(double mgdl, double expected)
    {
        _sut.CalculateHBGI([mgdl]).Should().BeApproximately(expected, expected * RelativeTolerance);
    }

    [Fact]
    public void RiskScale_SpansZeroToOneHundredAcrossTheMeasurableRange()
    {
        _sut.CalculateLBGI([20.0]).Should().BeInRange(99.0, 101.0);
        _sut.CalculateHBGI([600.0]).Should().BeInRange(99.0, 101.0);
    }

    /// <summary>
    /// A day spent entirely at 65 mg/dL is hypoglycaemic throughout, and the published scale puts
    /// it well past the high-risk band. A transform at under half the published magnitude reports
    /// it as moderate.
    /// </summary>
    [Fact]
    public void CalculateLBGI_AllReadingsHypoglycaemic_IsInTheHighRiskBand()
    {
        var values = Enumerable.Repeat(65.0, 288).ToArray();

        _sut.CalculateLBGI(values).Should().BeGreaterThan(5.0, "LBGI above 5 is the published high-risk band");
    }
}
