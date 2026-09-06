using FluentAssertions;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class DirectionWireFormatTests
{
    [Theory]
    [InlineData(Direction.NONE, "NONE")]
    [InlineData(Direction.TripleUp, "TripleUp")]
    [InlineData(Direction.DoubleUp, "DoubleUp")]
    [InlineData(Direction.SingleUp, "SingleUp")]
    [InlineData(Direction.FortyFiveUp, "FortyFiveUp")]
    [InlineData(Direction.Flat, "Flat")]
    [InlineData(Direction.FortyFiveDown, "FortyFiveDown")]
    [InlineData(Direction.SingleDown, "SingleDown")]
    [InlineData(Direction.DoubleDown, "DoubleDown")]
    [InlineData(Direction.TripleDown, "TripleDown")]
    [InlineData(Direction.NotComputable, "NOT COMPUTABLE")]
    [InlineData(Direction.RateOutOfRange, "RATE OUT OF RANGE")]
    [InlineData(Direction.CgmError, "CGM ERROR")]
    public void ToWireString_UsesLegacyNightscoutSpelling(Direction direction, string expected)
    {
        direction.ToWireString().Should().Be(expected);
    }

    [Fact]
    public void Parse_RoundTripsEveryWireString()
    {
        foreach (var direction in Enum.GetValues<Direction>())
        {
            DirectionExtensions.Parse(direction.ToWireString()).Should().Be(direction);
        }
    }

    [Theory]
    [InlineData("NOT COMPUTABLE", Direction.NotComputable)]
    [InlineData("NotComputable", Direction.NotComputable)]
    [InlineData("not computable", Direction.NotComputable)]
    [InlineData("notcomputable", Direction.NotComputable)]
    [InlineData("RATE OUT OF RANGE", Direction.RateOutOfRange)]
    [InlineData("RateOutOfRange", Direction.RateOutOfRange)]
    [InlineData("CGM ERROR", Direction.CgmError)]
    [InlineData("CgmError", Direction.CgmError)]
    [InlineData("NONE", Direction.NONE)]
    [InlineData("None", Direction.NONE)]
    [InlineData("Flat", Direction.Flat)]
    [InlineData("flat", Direction.Flat)]
    [InlineData("FORTYFIVEUP", Direction.FortyFiveUp)]
    public void Parse_AcceptsBothSpellings(string value, Direction expected)
    {
        DirectionExtensions.Parse(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sideways")]
    [InlineData("NOTCOMPUTABLE!")]
    public void TryParse_RejectsUnknownValues(string? value)
    {
        DirectionExtensions.TryParse(value, out var direction).Should().BeFalse();
        direction.Should().Be(Direction.NONE);
        DirectionExtensions.Parse(value).Should().Be(Direction.NONE);
    }

    [Fact]
    public void ParseToTrendNumber_UnknownAndEmptyValuesReportNotComputable()
    {
        DirectionExtensions.ParseToTrendNumber(null).Should().Be(8);
        DirectionExtensions.ParseToTrendNumber("").Should().Be(8);
        DirectionExtensions.ParseToTrendNumber("sideways").Should().Be(8);
    }

    [Theory]
    [InlineData("NOT COMPUTABLE", 8)]
    [InlineData("NotComputable", 8)]
    [InlineData("RATE OUT OF RANGE", 9)]
    [InlineData("RateOutOfRange", 9)]
    [InlineData("NONE", 0)]
    [InlineData("Flat", 4)]
    public void ParseToTrendNumber_AcceptsBothSpellings(string value, int expected)
    {
        DirectionExtensions.ParseToTrendNumber(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(GlucoseDirection.None, Direction.NONE)]
    [InlineData(GlucoseDirection.DoubleUp, Direction.DoubleUp)]
    [InlineData(GlucoseDirection.SingleUp, Direction.SingleUp)]
    [InlineData(GlucoseDirection.FortyFiveUp, Direction.FortyFiveUp)]
    [InlineData(GlucoseDirection.Flat, Direction.Flat)]
    [InlineData(GlucoseDirection.FortyFiveDown, Direction.FortyFiveDown)]
    [InlineData(GlucoseDirection.SingleDown, Direction.SingleDown)]
    [InlineData(GlucoseDirection.DoubleDown, Direction.DoubleDown)]
    [InlineData(GlucoseDirection.NotComputable, Direction.NotComputable)]
    [InlineData(GlucoseDirection.RateOutOfRange, Direction.RateOutOfRange)]
    public void ToDirection_MapsEveryV4Value(GlucoseDirection direction, Direction expected)
    {
        direction.ToDirection().Should().Be(expected);
    }

    [Theory]
    [InlineData(Direction.TripleUp)]
    [InlineData(Direction.TripleDown)]
    [InlineData(Direction.CgmError)]
    public void ToGlucoseDirection_IsNullForValuesV4DoesNotModel(Direction direction)
    {
        direction.ToGlucoseDirection().Should().BeNull();
    }

    [Fact]
    public void SensorGlucoseProjection_RoundTripsEveryDirection()
    {
        foreach (var direction in Enum.GetValues<GlucoseDirection>())
        {
            var entry = EntryProjection.FromSensorGlucose(
                new SensorGlucose { Mgdl = 120, Direction = direction }
            );

            entry.Direction.Should().Be(direction.ToWireString());
            entry.DirectionEnum.ToGlucoseDirection().Should().Be(direction);
        }
    }

    [Fact]
    public void SensorGlucoseProjection_EmitsLegacySpellingForSpaceSeparatedValues()
    {
        var entry = EntryProjection.FromSensorGlucose(
            new SensorGlucose { Mgdl = 120, Direction = GlucoseDirection.NotComputable }
        );

        entry.Direction.Should().Be("NOT COMPUTABLE");
        entry.DirectionEnum.Should().Be(Direction.NotComputable);
    }
}
