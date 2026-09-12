using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers the v3 consumable-change series, which share a point shape and one mapping pass. The
/// sensor-change series is the one a tracker needs to advance a CGM tracker automatically; it is
/// only populated for uploaders that report sensor lifecycle, so the empty and absent cases matter
/// as much as the populated one.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperV3DeviceEventTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperV3DeviceEventTests()
    {
        var logger = Mock.Of<ILogger>();
        var config = new GlookoConnectorConfiguration();
        var timeMapper = new GlookoTimeMapper(config, logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoV3GraphResponse BuildGraphData(
        GlookoV3ConsumableDataPoint[]? reservoirChange = null,
        GlookoV3ConsumableDataPoint[]? setSiteChange = null,
        GlookoV3ConsumableDataPoint[]? cgmSensorChange = null) =>
        new()
        {
            Series = new GlookoV3Series
            {
                ReservoirChange = reservoirChange,
                SetSiteChange = setSiteChange,
                CgmSensorChange = cgmSensorChange,
            }
        };

    private static GlookoV3ConsumableDataPoint Point(long x, string? label = null) =>
        new() { X = x, Label = label };

    [Fact]
    public void CgmSensorChange_MapsToSensorChangeDeviceEvent()
    {
        var graphData = BuildGraphData(cgmSensorChange: [Point(1781813092, "Sensor change")]);

        var events = _mapper.MapV3DeviceEvents(graphData);

        events.Should().ContainSingle();
        events[0].EventType.Should().Be(DeviceEventType.SensorChange);
        events[0].DataSource.Should().Be("glooko-connector");
        events[0].Notes.Should().Be("Sensor change");
        events[0].LegacyId.Should().StartWith("glooko_");
    }

    [Fact]
    public void EachConsumableSeries_MapsToItsOwnEventType()
    {
        var graphData = BuildGraphData(
            reservoirChange: [Point(1781813092)],
            setSiteChange: [Point(1781813093)],
            cgmSensorChange: [Point(1781813094)]);

        var events = _mapper.MapV3DeviceEvents(graphData);

        events.Select(e => e.EventType).Should().BeEquivalentTo([
            DeviceEventType.ReservoirChange,
            DeviceEventType.SiteChange,
            DeviceEventType.SensorChange,
        ]);
    }

    [Fact]
    public void SeriesSharingAnInstant_GetDistinctLegacyIds()
    {
        // The legacy id is the dedup key, and it is hashed from the series kind plus the timestamp —
        // so three changes logged at the same instant must not collapse into one stored event.
        var graphData = BuildGraphData(
            reservoirChange: [Point(1781813092)],
            setSiteChange: [Point(1781813092)],
            cgmSensorChange: [Point(1781813092)]);

        var events = _mapper.MapV3DeviceEvents(graphData);

        events.Select(e => e.LegacyId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EmptySensorSeries_ProducesNoEvents()
    {
        // Glooko returns cgmSensorChange as an empty series for uploaders that never report sensor
        // changes (CamAPS FX / Libre 3), rather than omitting it.
        var graphData = BuildGraphData(cgmSensorChange: []);

        _mapper.MapV3DeviceEvents(graphData).Should().BeEmpty();
    }

    [Fact]
    public void AbsentSensorSeries_ProducesNoEvents()
    {
        var graphData = BuildGraphData(reservoirChange: [Point(1781813092)]);

        var events = _mapper.MapV3DeviceEvents(graphData);

        events.Should().ContainSingle().Which.EventType.Should().Be(DeviceEventType.ReservoirChange);
    }

    [Fact]
    public void SensorChangeSeries_IsRequestedFromTheV3Graph()
    {
        // A mapping with no matching request is inert — the series has to be asked for by name.
        GlookoConstants.V3GraphSeries.Should().Contain("cgmSensorChange");
    }
}
