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
/// Covers <see cref="GlookoV4TreatmentMapper.MapSsv2ExtendedBoluses"/> — the SSV2
/// <c>pumps/extended_boluses</c> feed → Bolus (Square/Dual with a duration). Net-new vs v3.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperSsv2ExtendedBolusTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperSsv2ExtendedBolusTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    [Fact]
    public void MapSsv2ExtendedBoluses_AllExtended_IsSquare()
    {
        var boluses = _mapper.MapSsv2ExtendedBoluses([
            new GlookoExtendedBolus
            {
                PumpTimestamp = "2026-06-20T18:00:00.000Z",
                InsulinDelivered = 3.0, InitialDelivery = 0, ExtendedDelivery = 3.0,
                ExtendedBolusDuration = 120, Guid = "e-1",
            }
        ]);

        boluses.Should().ContainSingle();
        boluses[0].BolusType.Should().Be(BolusType.Square);
        boluses[0].Insulin.Should().Be(3.0);
        boluses[0].Duration.Should().Be(120);
        boluses[0].LegacyId.Should().Be("glooko_extended_bolus_e-1");
        boluses[0].SyncIdentifier.Should().Be(boluses[0].LegacyId);
    }

    [Fact]
    public void MapSsv2ExtendedBoluses_InitialPlusExtended_IsDual()
    {
        var boluses = _mapper.MapSsv2ExtendedBoluses([
            new GlookoExtendedBolus
            {
                PumpTimestamp = "2026-06-20T18:00:00.000Z",
                InsulinDelivered = 5.0, InitialDelivery = 2.0, ExtendedDelivery = 3.0,
                ExtendedBolusDuration = 90, Guid = "e-2",
            }
        ]);

        boluses[0].BolusType.Should().Be(BolusType.Dual);
        boluses[0].Insulin.Should().Be(5.0);
        boluses[0].Duration.Should().Be(90);
    }

    [Fact]
    public void MapSsv2ExtendedBoluses_FallsBackToSumWhenInsulinDeliveredMissing()
    {
        var boluses = _mapper.MapSsv2ExtendedBoluses([
            new GlookoExtendedBolus
            {
                PumpTimestamp = "2026-06-20T18:00:00.000Z",
                InsulinDelivered = 0, InitialDelivery = 1.5, ExtendedDelivery = 2.5,
                ExtendedBolusDuration = 60, Guid = "e-3",
            }
        ]);

        boluses[0].Insulin.Should().Be(4.0);
        boluses[0].BolusType.Should().Be(BolusType.Dual);
    }

    [Fact]
    public void MapSsv2ExtendedBoluses_ZeroDuration_LeavesDurationNull()
    {
        var boluses = _mapper.MapSsv2ExtendedBoluses([
            new GlookoExtendedBolus
            {
                PumpTimestamp = "2026-06-20T18:00:00.000Z",
                InsulinDelivered = 2.0, ExtendedDelivery = 2.0, ExtendedBolusDuration = 0, Guid = "e-4",
            }
        ]);

        boluses[0].Duration.Should().BeNull();
    }

    [Fact]
    public void MapSsv2ExtendedBoluses_SkipsSoftDeletedAndZeroTotal()
    {
        var boluses = _mapper.MapSsv2ExtendedBoluses([
            new GlookoExtendedBolus { PumpTimestamp = "2026-06-20T18:00:00.000Z", InsulinDelivered = 0, InitialDelivery = 0, ExtendedDelivery = 0, Guid = "z1" },
            new GlookoExtendedBolus { PumpTimestamp = "2026-06-20T18:00:00.000Z", InsulinDelivered = 5, Guid = "z2", SoftDeleted = true },
        ]);

        boluses.Should().BeEmpty();
    }
}
