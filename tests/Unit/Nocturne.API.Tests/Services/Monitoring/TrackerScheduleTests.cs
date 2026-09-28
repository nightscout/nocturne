using FluentAssertions;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerScheduleTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(TrackerMode.Duration, 240, 24, 24 * 60)]
    [InlineData(TrackerMode.Duration, 240, -24, 216 * 60)]
    [InlineData(TrackerMode.Duration, null, 24, 24 * 60)]
    [InlineData(TrackerMode.Event, null, -24, -24 * 60)]
    [InlineData(TrackerMode.Event, null, 2, 2 * 60)]
    public void OffsetMinutes_ResolvesAgainstTheModesReference(
        TrackerMode mode, int? lifespanHours, int hours, int expected)
    {
        TrackerSchedule.OffsetMinutes(mode, lifespanHours, hours).Should().Be(expected);
    }

    [Fact]
    public void OffsetMinutes_BeforeTheEndWithNoLifespan_NeverFires()
    {
        TrackerSchedule.OffsetMinutes(TrackerMode.Duration, null, -24).Should().BeNull();
    }

    [Fact]
    public void InstanceSchedule_PlacesABeforeTheEndStepAtTheEnd_NotBeforeTheStart()
    {
        var instance = Run(TrackerMode.Duration, lifespanHours: 240,
            (NotificationUrgency.Warn, -24), (NotificationUrgency.Urgent, 240));

        var schedule = TrackerInstanceDto.FromEntity(instance).Schedule;

        schedule.Select(s => (s.Urgency, s.FiresAt)).Should().Equal(
            (NotificationUrgency.Warn, Start.AddHours(216)),
            (NotificationUrgency.Urgent, Start.AddHours(240)));
    }

    [Fact]
    public void InstanceSchedule_ForAnEvent_CountsFromTheScheduledTime()
    {
        var scheduled = Start.AddDays(5);
        var instance = Run(TrackerMode.Event, lifespanHours: null, (NotificationUrgency.Info, -24));
        instance.ScheduledAt = scheduled;

        TrackerInstanceDto.FromEntity(instance).Schedule.Single().FiresAt
            .Should().Be(scheduled.AddHours(-24));
    }

    [Fact]
    public void InstanceSchedule_OmitsAStepThatCanNeverFire()
    {
        var instance = Run(TrackerMode.Duration, lifespanHours: null, (NotificationUrgency.Warn, -24));

        TrackerInstanceDto.FromEntity(instance).Schedule.Should().BeEmpty();
    }

    [Theory]
    [InlineData(240, 240, CompletionReason.Expired)]
    [InlineData(240, 100, CompletionReason.ReplacedEarly)]
    [InlineData(null, 100, CompletionReason.Completed)]
    public void ReasonForReplacement_ReadsTheRunAgainstItsLifespan(
        int? lifespanHours, int ranHours, CompletionReason expected)
    {
        TrackerSchedule.ReasonForReplacement(lifespanHours, Start, Start.AddHours(ranHours))
            .Should().Be(expected);
    }

    private static TrackerInstanceEntity Run(
        TrackerMode mode, int? lifespanHours, params (NotificationUrgency Urgency, int Hours)[] thresholds)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Sensor",
            Mode = mode,
            LifespanHours = lifespanHours,
        };
        foreach (var (urgency, hours) in thresholds)
        {
            definition.NotificationThresholds.Add(new TrackerNotificationThresholdEntity
            {
                Urgency = urgency,
                Hours = hours,
                DisplayOrder = definition.NotificationThresholds.Count,
            });
        }

        return new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            Definition = definition,
            StartedAt = Start,
        };
    }
}
