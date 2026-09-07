using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoV4TreatmentMapper.MapSsv2CarbsEvents"/> — the SSV2 <c>cgm/carbs_events</c>
/// feed (standalone app-logged carbs) → CarbIntake, the SSV2 counterpart to the v3 <c>carbAll</c> series.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperSsv2CarbsTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperSsv2CarbsTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    [Fact]
    public void MapSsv2CarbsEvents_MapsCarbsAndGuidKeyedId()
    {
        var carbs = _mapper.MapSsv2CarbsEvents([
            new GlookoSsv2CarbsEvent { Timestamp = "2026-06-20T12:30:00.000Z", CgmCarbs = 45, Guid = "c-9" }
        ]);

        carbs.Should().ContainSingle();
        carbs[0].Carbs.Should().Be(45);
        carbs[0].DataSource.Should().Be("glooko-connector");
        carbs[0].LegacyId.Should().Be("glooko_carbs_event_c-9");
        carbs[0].SyncIdentifier.Should().Be(carbs[0].LegacyId);
        carbs[0].Timestamp.Should().Be(new DateTime(2026, 6, 20, 12, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void MapSsv2CarbsEvents_FallsBackToEventTimeWhenTimestampMissing()
    {
        var carbs = _mapper.MapSsv2CarbsEvents([
            new GlookoSsv2CarbsEvent { Timestamp = null, EventTime = "2026-06-20T09:00:00.000Z", CgmCarbs = 20, Guid = "c-1" }
        ]);

        carbs.Should().ContainSingle();
        carbs[0].Timestamp.Should().Be(new DateTime(2026, 6, 20, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void MapSsv2CarbsEvents_NoGuid_FallsBackToHashedLegacyId()
    {
        var carbs = _mapper.MapSsv2CarbsEvents([
            new GlookoSsv2CarbsEvent { Timestamp = "2026-06-20T12:30:00.000Z", CgmCarbs = 30, Guid = null }
        ]);

        carbs[0].LegacyId.Should().StartWith("glooko_").And.NotContain("carbs_event_");
    }

    [Fact]
    public void MapSsv2CarbsEvents_SkipsSoftDeletedAndNonPositive()
    {
        var carbs = _mapper.MapSsv2CarbsEvents([
            new GlookoSsv2CarbsEvent { Timestamp = "2026-06-20T12:30:00.000Z", CgmCarbs = 0, Guid = "z" },
            new GlookoSsv2CarbsEvent { Timestamp = "2026-06-20T12:30:00.000Z", CgmCarbs = 25, Guid = "z2", SoftDeleted = true },
        ]);

        carbs.Should().BeEmpty();
    }
}
