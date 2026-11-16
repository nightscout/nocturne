using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseQualityRulesTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(SensorGlucoseQuality.Valid, true)]
    [InlineData(SensorGlucoseQuality.Uncertain, true)]
    [InlineData(SensorGlucoseQuality.Invalid, false)]
    public void CountsInStatistics_LeavesOutOnlyInvalid(SensorGlucoseQuality? quality, bool counts)
    {
        new SensorGlucose { Mgdl = 76, Quality = quality }.CountsInStatistics().Should().Be(counts);
    }
}
