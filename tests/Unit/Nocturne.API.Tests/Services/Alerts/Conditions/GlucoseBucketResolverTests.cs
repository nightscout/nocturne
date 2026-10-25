using FluentAssertions;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Conditions;

[Trait("Category", "Unit")]
public class GlucoseBucketResolverTests
{
    [Theory]
    // Defaults: vLow=54, low=70, tHigh=140, high=180, vHigh=250
    [InlineData(53.0, GlucoseBucket.VeryLow)]
    [InlineData(54.0, GlucoseBucket.Low)]    // 54 is Low (clinical convention)
    [InlineData(69.0, GlucoseBucket.Low)]
    [InlineData(70.0, GlucoseBucket.TightRange)]
    [InlineData(140.0, GlucoseBucket.TightRange)]
    [InlineData(141.0, GlucoseBucket.InRange)]
    [InlineData(180.0, GlucoseBucket.InRange)]
    [InlineData(181.0, GlucoseBucket.High)]
    [InlineData(250.0, GlucoseBucket.High)]
    [InlineData(251.0, GlucoseBucket.VeryHigh)]
    public void Boundaries_follow_the_default_thresholds(double glucose, GlucoseBucket expected)
    {
        GlucoseBucketResolver.Compute((decimal)glucose, 70m, 180m, null, null, null).Should().Be(expected);
    }
}
