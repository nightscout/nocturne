using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoStepCountMapper"/> — the SSV2 <c>validic/routines</c> daily-activity summary →
/// StepCount, with fractional steps rounded to an int, the absolute-total source flag, guid-keyed
/// deterministic ids, and soft-delete/zero skipping.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoStepCountMapperTests
{
    private readonly GlookoStepCountMapper _mapper;

    public GlookoStepCountMapperTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoStepCountMapper("glooko-connector", timeMapper, logger);
    }

    [Fact]
    public void MapSsv2Routines_RoundsFractionalStepsAndFlagsAbsoluteTotal()
    {
        var result = _mapper.MapSsv2Routines([new GlookoSsv2Routine
        {
            Steps = 6717.716,
            Timestamp = "2023-06-09T23:59:59.999Z",
            Source = "applehealth",
            Guid = "routine-guid-1",
        }]);

        result.Should().ContainSingle();
        result[0].Metric.Should().Be(6718);
        result[0].Source.Should().Be(1, "the daily routine total is an absolute count (bit 0 set), not a delta");
        result[0].DataSource.Should().Be("glooko-connector");
    }

    [Fact]
    public void MapSsv2Routines_KeysIdOnGuid_Deterministically()
    {
        var a = _mapper.MapSsv2Routines([new GlookoSsv2Routine { Steps = 100, Timestamp = "2023-06-09T23:59:59.999Z", Guid = "g1" }]);
        var b = _mapper.MapSsv2Routines([new GlookoSsv2Routine { Steps = 200, Timestamp = "2024-06-09T23:59:59.999Z", Guid = "g1" }]);

        a[0].Id.Should().Be(b[0].Id);
        Guid.TryParse(a[0].Id, out _).Should().BeTrue("the Id must round-trip to the entity primary key for upsert");
    }

    [Fact]
    public void MapSsv2Routines_SkipsSoftDeletedZeroAndNullSteps()
    {
        var result = _mapper.MapSsv2Routines(
        [
            new GlookoSsv2Routine { Steps = 5000, Timestamp = "2023-06-09T23:59:59.999Z", Guid = "g1", SoftDeleted = true },
            new GlookoSsv2Routine { Steps = 0, Timestamp = "2023-06-09T23:59:59.999Z", Guid = "g2" },
            new GlookoSsv2Routine { Steps = null, Timestamp = "2023-06-09T23:59:59.999Z", Guid = "g3" },
        ]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MapSsv2Routines_AppliesTimestampCorrection()
    {
        var result = _mapper.MapSsv2Routines([new GlookoSsv2Routine
        {
            Steps = 1234,
            Timestamp = "2023-06-09T12:00:00.000Z",
            Guid = "g1",
        }]);

        result[0].Timestamp.Should().Be(new DateTime(2023, 6, 9, 12, 0, 0, DateTimeKind.Utc));
    }
}
