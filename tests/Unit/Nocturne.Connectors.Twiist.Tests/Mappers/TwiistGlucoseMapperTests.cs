using FluentAssertions;
using Nocturne.Connectors.Twiist.Mappers;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Mappers;

public class TwiistGlucoseMapperTests
{
    [Theory]
    [InlineData("Flat", GlucoseDirection.Flat)]
    [InlineData("SingleUp", GlucoseDirection.SingleUp)]
    [InlineData("FortyFiveDown", GlucoseDirection.FortyFiveDown)]
    [InlineData("RateOutOfRange", GlucoseDirection.RateOutOfRange)]
    public void ParseTrend_ReadsTheSummaryTrendNames(string trend, GlucoseDirection expected)
    {
        TwiistGlucoseMapper.ParseTrend(trend).Should().Be(expected);
    }

    [Theory]
    [InlineData("UP", GlucoseDirection.SingleUp)]
    [InlineData("DOWN", GlucoseDirection.SingleDown)]
    [InlineData("DOUBLE_UP", GlucoseDirection.DoubleUp)]
    [InlineData("FORTY_FIVE_DOWN", GlucoseDirection.FortyFiveDown)]
    public void ParseTrend_ReadsTheArrowNames(string trend, GlucoseDirection expected)
    {
        TwiistGlucoseMapper.ParseTrend(trend).Should().Be(expected);
    }

    [Theory]
    [InlineData("flat", GlucoseDirection.Flat)]
    [InlineData("FLAT", GlucoseDirection.Flat)]
    [InlineData("fOrTyFiVeUp", GlucoseDirection.FortyFiveUp)]
    public void ParseTrend_IsCaseInsensitive(string trend, GlucoseDirection expected)
    {
        // The lookup uses OrdinalIgnoreCase, so a name that differs only in case is the same key —
        // listing both spellings as separate entries would throw when the table is built.
        TwiistGlucoseMapper.ParseTrend(trend).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, GlucoseDirection.None)]
    [InlineData("", GlucoseDirection.None)]
    public void ParseTrend_ReportsNoDirectionWhenTheSummaryCarriesNoTrend(string? trend, GlucoseDirection expected)
    {
        TwiistGlucoseMapper.ParseTrend(trend).Should().Be(expected);
    }

    [Fact]
    public void ParseTrend_ReportsNotComputableForAnUnrecognisedName()
    {
        TwiistGlucoseMapper.ParseTrend("sideways").Should().Be(GlucoseDirection.NotComputable);
    }
}
