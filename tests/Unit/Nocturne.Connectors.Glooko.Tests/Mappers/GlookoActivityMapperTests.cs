using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoActivityMapper"/> — the two SSV2 app-logged exercise feeds → Activity:
/// <c>exercises</c> (seconds duration, numeric intensity) and <c>cgm/exercise_events</c> (minutes
/// duration, string intensity). Both normalize duration to minutes.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoActivityMapperTests
{
    private readonly GlookoActivityMapper _mapper;

    public GlookoActivityMapperTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoActivityMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoSsv2Exercise Exercise(
        string? name = "Badminton",
        double duration = 3600,
        double? intensity = 50,
        string? guid = "C4210000-0000-0000-0000-000000000000",
        bool softDeleted = false) => new()
    {
        Name = name,
        Timestamp = "2026-03-09T18:30:00.000Z",
        Intensity = intensity,
        Duration = duration,
        Guid = guid,
        SoftDeleted = softDeleted,
    };

    private static GlookoSsv2ExerciseEvent ExerciseEvent(
        double duration = 30,
        string? intensity = "light",
        string? displayTime = "2021-11-14T16:15:00.000Z",
        string? eventTime = "2021-11-14T16:15:00.000Z",
        string? guid = "f45e0000-0000-0000-0000-000000000000",
        bool softDeleted = false) => new()
    {
        Duration = duration,
        Intensity = intensity,
        DisplayTime = displayTime,
        EventTime = eventTime,
        Guid = guid,
        SoftDeleted = softDeleted,
    };

    // ── exercises (seconds → minutes, numeric intensity) ──────────────────

    [Fact]
    public void Exercises_ConvertsSecondsToMinutes_AndStringifiesIntensity()
    {
        var activities = _mapper.MapSsv2Exercises([Exercise(duration: 3600, intensity: 50)]);

        activities.Should().ContainSingle();
        var a = activities[0];
        a.Duration.Should().Be(60, "3600 seconds = 60 minutes");
        a.Intensity.Should().Be("50");
        a.Name.Should().Be("Badminton");
        a.Type.Should().Be("exercise");
        a.EnteredBy.Should().Be("glooko-connector");
        // Default config (offset 0, no timeline) → fake-UTC wall-clock preserved.
        a.Mills.Should().Be(new DateTimeOffset(2026, 3, 9, 18, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Exercises_KeysIdOnGuid()
    {
        var activities = _mapper.MapSsv2Exercises([Exercise(guid: "abc-123")]);

        activities[0].Id.Should().Be("glooko_exercise_abc-123");
    }

    [Fact]
    public void Exercises_NoGuid_FallsBackToHashedId()
    {
        var activities = _mapper.MapSsv2Exercises([Exercise(guid: null)]);

        activities[0].Id.Should().StartWith("glooko_").And.NotContain("exercise_");
    }

    [Fact]
    public void Exercises_SkipsSoftDeleted()
    {
        var activities = _mapper.MapSsv2Exercises([Exercise(softDeleted: true)]);

        activities.Should().BeEmpty();
    }

    // ── exercise_events (minutes, string intensity) ───────────────────────

    [Fact]
    public void ExerciseEvents_KeepsMinutes_AndStringIntensity()
    {
        var activities = _mapper.MapSsv2ExerciseEvents([ExerciseEvent(duration: 30, intensity: "light")]);

        activities.Should().ContainSingle();
        var a = activities[0];
        a.Duration.Should().Be(30, "exercise_events duration is already in minutes");
        a.Intensity.Should().Be("light");
        a.Type.Should().Be("exercise");
        a.Mills.Should().Be(new DateTimeOffset(2021, 11, 14, 16, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ExerciseEvents_KeysIdOnGuid()
    {
        var activities = _mapper.MapSsv2ExerciseEvents([ExerciseEvent(guid: "xyz-789")]);

        activities[0].Id.Should().Be("glooko_exercise_event_xyz-789");
    }

    [Fact]
    public void ExerciseEvents_PrefersDisplayTimeOverEventTime()
    {
        var activities = _mapper.MapSsv2ExerciseEvents(
        [
            ExerciseEvent(
                displayTime: "2021-11-14T16:15:00.000Z",
                eventTime: "2019-01-01T00:00:00.000Z"),
        ]);

        activities[0].Mills.Should().Be(
            new DateTimeOffset(2021, 11, 14, 16, 15, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ExerciseEvents_SkipsSoftDeleted()
    {
        var activities = _mapper.MapSsv2ExerciseEvents([ExerciseEvent(softDeleted: true)]);

        activities.Should().BeEmpty();
    }
}
