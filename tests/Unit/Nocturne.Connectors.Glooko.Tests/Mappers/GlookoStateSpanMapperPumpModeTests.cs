using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

public class GlookoStateSpanMapperPumpModeTests
{
    private const string ConnectorSource = "glooko_test";
    private readonly GlookoStateSpanMapper _mapper;

    public GlookoStateSpanMapperPumpModeTests()
    {
        var config = new GlookoConnectorConfiguration { TimezoneOffset = 0 };
        var timeMapper = new GlookoTimeMapper(config, NullLogger.Instance);
        _mapper = new GlookoStateSpanMapper(ConnectorSource, timeMapper, NullLogger.Instance);
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsEaseOff()
    {
        var response = BuildResponse("PumpCamapsEaseOffMode", "ease_off", 3600);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Category.Should().Be(StateSpanCategory.PumpMode);
        span.State.Should().Be("EaseOff");
        span.EndTimestamp.Should().NotBeNull();
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsLiberty()
    {
        var response = BuildResponse("PumpCamapsLibertyMode", "liberty", 7200);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Category.Should().Be(StateSpanCategory.PumpMode);
        span.State.Should().Be("Liberty");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsControlIqSleep()
    {
        var response = BuildResponse("PumpControliqSleepMode", "sleep", 28800);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        spans[0].State.Should().Be("Sleep");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsOp5Limited()
    {
        var response = BuildResponse("PumpOp5LimitedMode", "limited", 1800);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        spans[0].State.Should().Be("Limited");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_SkipsInterpolatedPoints()
    {
        var series = new GlookoV3Series();
        SetSeriesProperty(series, "PumpCamapsAutomaticMode", [
            new GlookoV3PumpModeDataPoint { X = 1000, Type = "automatic", Duration = 3600, Interpolated = true },
            new GlookoV3PumpModeDataPoint { X = 2000, Type = "automatic", Duration = 3600, Interpolated = false },
            new GlookoV3PumpModeDataPoint { X = 3000, Type = "automatic", Duration = 3600, Interpolated = true },
        ]);

        var spans = _mapper.TransformV3PumpModeToStateSpans(new GlookoV3GraphResponse { Series = series });

        spans.Should().HaveCount(1);
        spans[0].OriginalId.Should().Contain("2000");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsConnectivity()
    {
        var response = BuildResponse("PumpCamapsNoPumpConnectivityMode", "no_pump_connectivity", 600);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Category.Should().Be(StateSpanCategory.PumpConnectivity);
        span.State.Should().Be("Disconnected");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_MapsBluetoothOff()
    {
        var response = BuildResponse("PumpCamapsBluetoothTurnedOffMode", "bluetooth_turned_off", 300);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        var span = spans[0];
        span.Category.Should().Be(StateSpanCategory.PumpConnectivity);
        span.State.Should().Be("BluetoothOff");
    }

    [Fact]
    public void TransformV3PumpModeToStateSpans_NullSeriesReturnsEmpty()
    {
        var response = new GlookoV3GraphResponse { Series = null };

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void TransformV3PumpModeToStateSpans_NullOrZeroDuration_ProducesNullEndTimestamp(int? duration)
    {
        var response = BuildResponse("PumpCamapsAutomaticMode", "automatic", duration);

        var spans = _mapper.TransformV3PumpModeToStateSpans(response);

        spans.Should().HaveCount(1);
        spans[0].EndTimestamp.Should().BeNull(
            because: "a null or zero duration means the span has no known end");
    }

    private static GlookoV3GraphResponse BuildResponse(string seriesProperty, string type, int? duration)
    {
        var series = new GlookoV3Series();
        SetSeriesProperty(series, seriesProperty, [
            new GlookoV3PumpModeDataPoint
            {
                X = 1779230416,
                Type = type,
                Duration = duration,
                Interpolated = false,
            }
        ]);
        return new GlookoV3GraphResponse { Series = series };
    }

    private static void SetSeriesProperty(GlookoV3Series series, string name, GlookoV3PumpModeDataPoint[] value)
    {
        var prop = typeof(GlookoV3Series).GetProperty(name)
            ?? throw new ArgumentException($"No property {name} on GlookoV3Series");
        prop.SetValue(series, value);
    }
}
