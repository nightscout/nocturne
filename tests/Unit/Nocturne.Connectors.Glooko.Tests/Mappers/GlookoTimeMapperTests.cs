using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Verifies both directions of the fake-UTC conversion honour the timezone timeline, so the request
/// window sent to Glooko is built in the same wall-clock space the responses are interpreted in.
/// </summary>
public class GlookoTimeMapperTests
{
    /// <summary>
    /// Timeline-bearing cases pass a non-zero static offset that matches no zone under test, so a
    /// mapper that added the legacy offset to the timeline result instead of replacing it would fail.
    /// </summary>
    private const double DecoyOffset = 3;

    private static GlookoTimeMapper Mapper(TimezoneTimeline? timeline, double offset = DecoyOffset)
    {
        var mapper = new GlookoTimeMapper(
            new GlookoConnectorConfiguration { TimezoneOffset = offset }, NullLogger.Instance);
        if (timeline is not null)
            mapper.UseTimeline(timeline);
        return mapper;
    }

    private static TimezoneTimeline Sydney() =>
        new([new TimezoneTimelineEntry { Timezone = "Australia/Sydney", EffectiveFrom = DateTime.MinValue }]);

    [Fact]
    public void ToGlookoTime_InDaylightSaving_UsesTheSummerOffset()
    {
        // 2026-01-09 13:00Z is AEDT (+11) -> local midnight on the 10th.
        var result = Mapper(Sydney()).ToGlookoTime(new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc));

        result.Should().Be(new DateTime(2026, 1, 10, 0, 0, 0));
    }

    [Fact]
    public void ToGlookoTime_OutsideDaylightSaving_UsesTheWinterOffset()
    {
        // Same wall-clock target six months later, when Sydney is AEST (+10).
        var result = Mapper(Sydney()).ToGlookoTime(new DateTime(2026, 7, 9, 14, 0, 0, DateTimeKind.Utc));

        result.Should().Be(new DateTime(2026, 7, 10, 0, 0, 0));
    }

    [Fact]
    public void ToGlookoTime_AcrossRelocation_UsesTheZoneInEffectAtEachInstant()
    {
        var mapper = Mapper(new TimezoneTimeline(
        [
            new TimezoneTimelineEntry { Timezone = "Australia/Sydney", EffectiveFrom = DateTime.MinValue },
            new TimezoneTimelineEntry { Timezone = "Europe/London", EffectiveFrom = new DateTime(2026, 3, 1) },
        ]));

        // Before the move: Sydney, AEDT (+11).
        mapper.ToGlookoTime(new DateTime(2026, 2, 15, 13, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 2, 16, 0, 0, 0));

        // After the move: London, BST (+1). Picking the newest entry regardless of instant would
        // return the London offset for both.
        mapper.ToGlookoTime(new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 6, 10, 10, 0, 0));
    }

    [Fact]
    public void ToGlookoTime_NoTimeline_FallsBackToStaticOffset()
    {
        var result = Mapper(timeline: null, offset: 10)
            .ToGlookoTime(new DateTime(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc));

        result.Should().Be(new DateTime(2026, 1, 9, 23, 0, 0));
    }

    /// <summary>
    /// Round-tripping is only lossless away from transition boundaries: <see cref="TimezoneTimeline.ToLocal"/>
    /// resolves entries in wall-clock space, so near a relocation or a fall-back overlap the pair is off
    /// by the zone delta. The request window is padded a day either side to absorb that.
    /// </summary>
    [Theory]
    [InlineData(2026, 1, 9)]
    [InlineData(2026, 7, 9)]
    public void ToGlookoTime_AwayFromTransitions_RoundTripsThroughGetCorrectedGlookoTime(
        int year, int month, int day)
    {
        var mapper = Mapper(Sydney());
        var utc = new DateTime(year, month, day, 13, 0, 0, DateTimeKind.Utc);

        mapper.GetCorrectedGlookoTime(mapper.ToGlookoTime(utc)).Should().Be(utc);
    }
}
