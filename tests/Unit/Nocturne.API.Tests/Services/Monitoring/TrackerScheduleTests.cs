using FluentAssertions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
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

        var schedule = TrackerInstanceDto.From(instance, Start).Schedule;

        schedule.Select(s => (s.Urgency, s.FiresAt)).Should().Equal(
            (NotificationUrgency.Warn, Start.AddHours(216)),
            (NotificationUrgency.Urgent, Start.AddHours(240)));
    }

    [Fact]
    public void InstanceSchedule_ForAnEvent_CountsFromTheScheduledTime()
    {
        var scheduled = Start.AddDays(5);
        var instance = Run(TrackerMode.Event, lifespanHours: null, (NotificationUrgency.Info, -24)) with
        {
            ScheduledAt = scheduled,
        };

        TrackerInstanceDto.From(instance, Start).Schedule.Single().FiresAt
            .Should().Be(scheduled.AddHours(-24));
    }

    [Fact]
    public void InstanceSchedule_OmitsAStepThatCanNeverFire()
    {
        var instance = Run(TrackerMode.Duration, lifespanHours: null, (NotificationUrgency.Warn, -24));

        TrackerInstanceDto.From(instance, Start).Schedule.Should().BeEmpty();
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

    private static TrackerInstance Run(
        TrackerMode mode, int? lifespanHours, params (NotificationUrgency Urgency, int Hours)[] thresholds) =>
        new()
        {
            Id = Guid.NewGuid(),
            Definition = TrackerDefinition.Restore(
                Guid.NewGuid(),
                "Sensor",
                mode: mode,
                lifespanHours: lifespanHours,
                notificationThresholds:
                [
                    .. thresholds.Select((t, order) =>
                        new TrackerNotificationThreshold(Guid.NewGuid(), t.Urgency, t.Hours, null, order)),
                ]),
            StartedAt = Start,
        };
}
