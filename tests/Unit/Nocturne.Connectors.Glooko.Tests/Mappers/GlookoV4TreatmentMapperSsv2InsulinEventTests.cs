using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoV4TreatmentMapper.MapSsv2InsulinEvents"/> — the SSV2 <c>cgm/insulin_events</c>
/// feed (app-logged MDI insulin doses): "fast_acting" → rapid Bolus, "long_acting"/"intermediate" →
/// BasalInjection, unknown → Bolus.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperSsv2InsulinEventTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperSsv2InsulinEventTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoSsv2InsulinEvent Event(
        double insulin,
        string? insulinType,
        string? displayTime = "2021-11-14T17:06:00.000Z",
        string? eventTime = "2021-11-14T17:06:00.000Z",
        string? guid = "f458aaaa-0000-0000-0000-000000000000",
        bool softDeleted = false) => new()
    {
        Insulin = insulin,
        InsulinType = insulinType,
        DisplayTime = displayTime,
        EventTime = eventTime,
        Guid = guid,
        SoftDeleted = softDeleted,
    };

    [Fact]
    public void FastActing_MapsToRapidBolus()
    {
        var (basals, boluses) = _mapper.MapSsv2InsulinEvents([Event(4.5, "fast_acting")]);

        basals.Should().BeEmpty();
        boluses.Should().ContainSingle();
        var b = boluses[0];
        b.Insulin.Should().Be(4.5);
        b.BolusType.Should().Be(BolusType.Normal);
        b.Automatic.Should().BeFalse();
        b.DataSource.Should().Be("glooko-connector");
        b.InsulinContext.Should().NotBeNull();
    }

    [Theory]
    [InlineData("long_acting")]
    [InlineData("intermediate")]
    [InlineData("basal")]
    public void LongActingTypes_MapToBasalInjection(string insulinType)
    {
        var (basals, boluses) = _mapper.MapSsv2InsulinEvents([Event(22, insulinType)]);

        boluses.Should().BeEmpty();
        basals.Should().ContainSingle();
        basals[0].Units.Should().Be(22);
        basals[0].InsulinContext.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something_unexpected")]
    public void UnknownType_DefaultsToBolus(string? insulinType)
    {
        var (basals, boluses) = _mapper.MapSsv2InsulinEvents([Event(2, insulinType)]);

        basals.Should().BeEmpty();
        boluses.Should().ContainSingle();
    }

    [Fact]
    public void KeysLegacyIdOnGuid_AndSetsSyncIdentifier()
    {
        var (_, boluses) = _mapper.MapSsv2InsulinEvents([Event(3, "fast_acting", guid: "abc-123")]);

        boluses[0].LegacyId.Should().Be("glooko_insulin_event_bolus_abc-123");
        boluses[0].SyncIdentifier.Should().Be(boluses[0].LegacyId);
    }

    [Fact]
    public void BasalKeysLegacyIdOnGuid()
    {
        var (basals, _) = _mapper.MapSsv2InsulinEvents([Event(10, "long_acting", guid: "def-456")]);

        basals[0].LegacyId.Should().Be("glooko_insulin_event_basal_def-456");
        basals[0].SyncIdentifier.Should().Be(basals[0].LegacyId);
    }

    [Fact]
    public void NoGuid_FallsBackToHashedLegacyId()
    {
        var (_, boluses) = _mapper.MapSsv2InsulinEvents([Event(3, "fast_acting", guid: null)]);

        boluses[0].LegacyId.Should().StartWith("glooko_").And.NotContain("insulin_event_bolus_");
    }

    [Fact]
    public void SkipsSoftDeletedAndNonPositive()
    {
        var (basals, boluses) = _mapper.MapSsv2InsulinEvents(
        [
            Event(0, "fast_acting"),
            Event(5, "fast_acting", softDeleted: true),
            Event(-1, "long_acting"),
            Event(7, "long_acting", softDeleted: true),
        ]);

        basals.Should().BeEmpty();
        boluses.Should().BeEmpty();
    }

    [Fact]
    public void PrefersDisplayTimeOverEventTime()
    {
        var (_, boluses) = _mapper.MapSsv2InsulinEvents(
        [
            Event(2, "fast_acting",
                displayTime: "2021-11-14T17:06:00.000Z",
                eventTime: "2020-01-01T00:00:00.000Z"),
        ]);

        // Default config (offset 0, no timeline) → fake-UTC wall-clock preserved from display_time.
        boluses[0].Timestamp.Should().Be(new DateTime(2021, 11, 14, 17, 6, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void FallsBackToEventTimeWhenDisplayTimeMissing()
    {
        var (_, boluses) = _mapper.MapSsv2InsulinEvents(
        [
            Event(2, "fast_acting", displayTime: null, eventTime: "2020-01-01T00:00:00.000Z"),
        ]);

        boluses[0].Timestamp.Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
