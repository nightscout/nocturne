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
/// Covers <see cref="GlookoV4TreatmentMapper.MapSsv2InjectionInsulin"/> — the SSV2 pen-injection feeds
/// (injection_boluses → Bolus, injection_basals → BasalInjection), the SSV2 counterpart to the v3
/// gkInsulin* series and the only insulin source for MDI users.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoV4TreatmentMapperSsv2InjectionTests
{
    private readonly GlookoV4TreatmentMapper _mapper;

    public GlookoV4TreatmentMapperSsv2InjectionTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoV4TreatmentMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoInjectionInsulin Injection(
        double units, string? name, string? guid = "11111111-1111-1111-1111-111111111111", bool softDeleted = false) => new()
    {
        PumpTimestamp = "2026-06-20T08:00:00.000Z",
        InsulinDelivered = units,
        Name = name,
        Guid = guid,
        SoftDeleted = softDeleted,
    };

    [Fact]
    public void MapSsv2InjectionInsulin_Bolus_MapsUnitsNameAndContext()
    {
        var (basals, boluses) = _mapper.MapSsv2InjectionInsulin([], [Injection(4.5, "Humalog")]);

        basals.Should().BeEmpty();
        boluses.Should().ContainSingle();
        var b = boluses[0];
        b.Insulin.Should().Be(4.5);
        b.InsulinType.Should().Be("Humalog");
        b.BolusType.Should().Be(BolusType.Normal);
        b.Automatic.Should().BeFalse();
        b.DataSource.Should().Be("glooko-connector");
        b.InsulinContext.Should().NotBeNull("a known insulin should resolve DIA/peak from the catalog");
    }

    [Fact]
    public void MapSsv2InjectionInsulin_Basal_MapsUnitsAndLongActingContext()
    {
        var (basals, boluses) = _mapper.MapSsv2InjectionInsulin([Injection(22, "Tresiba®U100")], []);

        boluses.Should().BeEmpty();
        basals.Should().ContainSingle();
        basals[0].Units.Should().Be(22);
        basals[0].InsulinContext.Should().NotBeNull();
    }

    [Fact]
    public void MapSsv2InjectionInsulin_KeysLegacyIdOnGuid_AndSetsSyncIdentifier()
    {
        var (_, boluses) = _mapper.MapSsv2InjectionInsulin([], [Injection(3, "NovoRapid", guid: "abc-123")]);

        boluses[0].LegacyId.Should().Be("glooko_injection_bolus_abc-123");
        boluses[0].SyncIdentifier.Should().Be(boluses[0].LegacyId, "re-correction must upsert in place");
    }

    [Fact]
    public void MapSsv2InjectionInsulin_NoGuid_FallsBackToHashedLegacyId()
    {
        var (basals, _) = _mapper.MapSsv2InjectionInsulin([Injection(10, "Lantus", guid: null)], []);

        basals[0].LegacyId.Should().StartWith("glooko_").And.NotContain("injection_basal_");
    }

    [Fact]
    public void MapSsv2InjectionInsulin_SkipsSoftDeletedAndNonPositiveUnits()
    {
        var (basals, boluses) = _mapper.MapSsv2InjectionInsulin(
            [Injection(0, "Tresiba"), Injection(15, "Tresiba", softDeleted: true)],
            [Injection(-1, "Humalog"), Injection(0, "Humalog", softDeleted: true)]);

        basals.Should().BeEmpty();
        boluses.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2InjectionInsulin_AppliesTimestampCorrection()
    {
        var (_, boluses) = _mapper.MapSsv2InjectionInsulin([], [Injection(2, "Humalog")]);

        // Default config (offset 0, no timeline) → fake-UTC wall-clock preserved.
        boluses[0].Timestamp.Should().Be(new DateTime(2026, 6, 20, 8, 0, 0, DateTimeKind.Utc));
    }
}
