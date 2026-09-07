using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoBodyWeightMapper"/> — the two SSV2 weight feeds (manual/HealthKit <c>weights</c>
/// in grams, and third-party <c>validic/weights</c> in kilograms), both → BodyWeight (kilograms), with
/// guid-keyed deterministic ids, soft-delete skipping, and non-positive skipping.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoBodyWeightMapperTests
{
    private readonly GlookoBodyWeightMapper _mapper;

    public GlookoBodyWeightMapperTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoBodyWeightMapper("glooko-connector", timeMapper, logger);
    }

    [Fact]
    public void MapSsv2Weights_ConvertsGramsToKilograms()
    {
        var result = _mapper.MapSsv2Weights([new GlookoSsv2Weight
        {
            Value = 86700, // grams
            WeightUnit = "kg",
            Timestamp = "2023-06-03T00:00:00.000Z",
            Guid = "weight-guid-1",
        }]);

        result.Should().ContainSingle();
        result[0].WeightKg.Should().Be(86.7m);
        result[0].DataSource.Should().Be("glooko-connector");
        result[0].EnteredBy.Should().Be("glooko-connector");
    }

    [Fact]
    public void MapSsv2ValidicWeights_UsesKilogramsDirectly()
    {
        var result = _mapper.MapSsv2ValidicWeights([new GlookoSsv2ValidicWeight
        {
            Weight = 68, // already kg
            Bmi = 23.54,
            Timestamp = "2020-12-25T23:59:59.000Z",
            Source = "fitbit",
            Guid = "validic-guid-1",
        }]);

        result.Should().ContainSingle();
        result[0].WeightKg.Should().Be(68m);
    }

    [Fact]
    public void MapSsv2Weights_KeysIdOnGuid_Deterministically()
    {
        var a = _mapper.MapSsv2Weights([new GlookoSsv2Weight { Value = 80000, Timestamp = "2023-06-03T00:00:00.000Z", Guid = "g1" }]);
        var b = _mapper.MapSsv2Weights([new GlookoSsv2Weight { Value = 81000, Timestamp = "2024-01-01T00:00:00.000Z", Guid = "g1" }]);

        // Same guid ⇒ same deterministic Id (re-sync upserts in place), and it must be a real GUID.
        a[0].Id.Should().Be(b[0].Id);
        Guid.TryParse(a[0].Id, out _).Should().BeTrue("the Id must round-trip to the entity primary key for upsert");
    }

    [Fact]
    public void MapSsv2Weights_SkipsSoftDeletedAndNonPositive()
    {
        var result = _mapper.MapSsv2Weights(
        [
            new GlookoSsv2Weight { Value = 80000, Timestamp = "2023-06-03T00:00:00.000Z", Guid = "g1", SoftDeleted = true },
            new GlookoSsv2Weight { Value = 0, Timestamp = "2023-06-03T00:00:00.000Z", Guid = "g2" },
        ]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2ValidicWeights_SkipsSoftDeletedAndMissingWeight()
    {
        var result = _mapper.MapSsv2ValidicWeights(
        [
            new GlookoSsv2ValidicWeight { Weight = 70, Timestamp = "2020-12-25T23:59:59.000Z", Guid = "g1", SoftDeleted = true },
            new GlookoSsv2ValidicWeight { Weight = null, Timestamp = "2020-12-25T23:59:59.000Z", Guid = "g2" },
        ]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2Weights_AppliesTimestampCorrection()
    {
        var result = _mapper.MapSsv2Weights([new GlookoSsv2Weight
        {
            Value = 70000,
            Timestamp = "2023-06-03T08:30:00.000Z",
            Guid = "g1",
        }]);

        // Default config (offset 0, no timeline) → fake-UTC wall-clock preserved.
        var expectedMills = new DateTimeOffset(2023, 6, 3, 8, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        result[0].Mills.Should().Be(expectedMills);
    }
}
