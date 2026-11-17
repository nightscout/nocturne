using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private static GlookoSensorGlucoseMapper MapperWith(ILogger logger)
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = 0 };
        return new GlookoSensorGlucoseMapper(
            config, ConnectorSource, new GlookoTimeMapper(config, NullLogger.Instance), logger, TimeProvider.System);
    }

    private static GlookoBatchData BatchOf(params string?[] trends) =>
        new()
        {
            Readings =
            [
                .. trends.Select((trend, i) => new GlookoCgmReading
                {
                    Timestamp = $"2026-03-09T07:0{i}:00.000Z",
                    Value = 12000,
                    Trend = trend,
                }),
            ],
        };

    private static GlucoseDirection? DirectionFor(string? trend) =>
        MapperWith(NullLogger.Instance)
            .TransformBatchDataToSensorGlucose(BatchOf(trend))
            .Single()
            .Direction;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTrendSupplied_HasNoDirection(string? trend) =>
        DirectionFor(trend).Should().BeNull();

    [Theory]
    [InlineData("SIDEWAYS")]
    [InlineData("42")]
    public void UnrecognisedTrend_HasNoDirection(string trend) =>
        DirectionFor(trend).Should().BeNull();

    [Fact]
    public void UnrecognisedTrend_IsLoggedOncePerDistinctValue()
    {
        var logger = new RecordingLogger();

        MapperWith(logger).TransformBatchDataToSensorGlucose(BatchOf("SIDEWAYS", "sideways", "SIDEWAYS", "42"));

        logger.Messages.Where(m => m.Contains("Unrecognised")).Should().HaveCount(2);
        logger.Messages.Should().ContainSingle(m => m.Contains("'SIDEWAYS'"));
        logger.Messages.Should().ContainSingle(m => m.Contains("'42'"));
    }

    [Fact]
    public void UnrecognisedTrend_IsLoggedOncePerSyncRun()
    {
        var logger = new RecordingLogger();

        MapperWith(logger).TransformBatchDataToSensorGlucose(BatchOf("SIDEWAYS"));
        MapperWith(logger).TransformBatchDataToSensorGlucose(BatchOf("SIDEWAYS"));

        logger.Messages.Where(m => m.Contains("'SIDEWAYS'")).Should().HaveCount(2);
    }

    [Fact]
    public void RecognisedAndBlankTrends_AreNotLoggedAsUnrecognised()
    {
        var logger = new RecordingLogger();

        MapperWith(logger).TransformBatchDataToSensorGlucose(BatchOf("FLAT", null, "", "NOT COMPUTABLE"));

        logger.Messages.Should().NotContain(m => m.Contains("Unrecognised"));
    }

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
