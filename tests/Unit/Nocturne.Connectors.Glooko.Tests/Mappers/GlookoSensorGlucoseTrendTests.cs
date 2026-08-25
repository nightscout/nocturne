using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

public class GlookoSensorGlucoseTrendTests
{
    private const string ConnectorSource = "glooko_test";

    private static GlucoseDirection? DirectionFor(string? trend)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = 0 };
        var mapper = new GlookoSensorGlucoseMapper(
            config, ConnectorSource, new GlookoTimeMapper(config, NullLogger.Instance), NullLogger.Instance);

        var batch = new GlookoBatchData
        {
            Readings =
            [
                new GlookoCgmReading { Timestamp = "2026-03-09T07:00:00.000Z", Value = 12000, Trend = trend },
            ],
        };

        return mapper.TransformBatchDataToSensorGlucose(batch).Single().Direction;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTrendSupplied_IsNone(string? trend) =>
        DirectionFor(trend).Should().Be(GlucoseDirection.None);

    [Theory]
    [InlineData("SIDEWAYS")]
    [InlineData("42")]
    public void UnrecognisedTrend_IsNotComputable(string trend) =>
        DirectionFor(trend).Should().Be(GlucoseDirection.NotComputable);

    [Theory]
    [InlineData("FLAT", GlucoseDirection.Flat)]
    [InlineData("flat", GlucoseDirection.Flat)]
    [InlineData("DOUBLEUP", GlucoseDirection.DoubleUp)]
    [InlineData("DOUBLE_UP", GlucoseDirection.DoubleUp)]
    [InlineData("SINGLEUP", GlucoseDirection.SingleUp)]
    [InlineData("FORTYFIVEUP", GlucoseDirection.FortyFiveUp)]
    [InlineData("FORTY_FIVE_DOWN", GlucoseDirection.FortyFiveDown)]
    [InlineData("SINGLEDOWN", GlucoseDirection.SingleDown)]
    [InlineData("DOUBLE_DOWN", GlucoseDirection.DoubleDown)]
    [InlineData("NOT COMPUTABLE", GlucoseDirection.NotComputable)]
    [InlineData("RATEOUTOFRANGE", GlucoseDirection.RateOutOfRange)]
    public void RecognisedTrend_MapsToItsDirection(string trend, GlucoseDirection expected) =>
        DirectionFor(trend).Should().Be(expected);
}
