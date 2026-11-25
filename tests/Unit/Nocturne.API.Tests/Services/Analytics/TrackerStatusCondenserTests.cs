using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Widget;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class TrackerStatusCondenserTests
{
    private static readonly DateTime Reference = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    public static TheoryData<TrackerMode, int?, int, int, NotificationUrgency> Edges()
    {
        var data = new TheoryData<TrackerMode, int?, int, int, NotificationUrgency>();

        foreach (var (offset, expected) in Around(NotificationUrgency.Warn))
        {
            data.Add(TrackerMode.Duration, 240, 24, 24 * 60 + offset, expected);
            data.Add(TrackerMode.Duration, null, 24, 24 * 60 + offset, expected);
            data.Add(TrackerMode.Duration, 240, -24, 216 * 60 + offset, expected);
            data.Add(TrackerMode.Event, null, -24, -24 * 60 + offset, expected);
            data.Add(TrackerMode.Event, null, 2, 2 * 60 + offset, expected);
            data.Add(TrackerMode.Event, null, 0, offset, expected);
        }

        data.Add(TrackerMode.Duration, null, -24, 0, NotificationUrgency.Info);
        data.Add(TrackerMode.Duration, null, -24, 1_000_000, NotificationUrgency.Info);
        data.Add(TrackerMode.Event, null, -24, 60, NotificationUrgency.Warn);
        data.Add(TrackerMode.Event, null, -24, 24 * 60, NotificationUrgency.Warn);
        data.Add(TrackerMode.Event, null, 2, -60, NotificationUrgency.Info);

        return data;
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void ScheduleAndCondenser_ReadTheSameUrgency_AtEachEdge(
        TrackerMode mode, int? lifespanHours, int thresholdHours, int minutesFromReference,
        NotificationUrgency expected)
    {
        var instance = Run(mode, lifespanHours, (NotificationUrgency.Warn, thresholdHours));
        var now = Reference.AddMinutes(minutesFromReference);

        TrackerSchedule.UrgencyAt(instance, now).Should().Be(expected);
        Condense(instance, now).Urgency.Should().Be(expected);
    }

    [Fact]
    public void ScheduleAndCondenser_TakeTheHighestUrgencyFired()
    {
        var instance = Run(TrackerMode.Duration, 240,
            (NotificationUrgency.Urgent, 240), (NotificationUrgency.Warn, -24), (NotificationUrgency.Hazard, 230));

        var now = Reference.AddHours(225);

        TrackerSchedule.UrgencyAt(instance, now).Should().Be(NotificationUrgency.Warn);
        Condense(instance, now).Urgency.Should().Be(NotificationUrgency.Warn);

        now = Reference.AddHours(231);
        TrackerSchedule.UrgencyAt(instance, now).Should().Be(NotificationUrgency.Hazard);
        Condense(instance, now).Urgency.Should().Be(NotificationUrgency.Hazard);
    }

    [Fact]
    public void ScheduleAndCondenser_WithNoThresholds_ReadInfo()
    {
        var instance = Run(TrackerMode.Duration, 240);

        TrackerSchedule.UrgencyAt(instance, Reference.AddHours(500)).Should().Be(NotificationUrgency.Info);
        Condense(instance, Reference.AddHours(500)).Urgency.Should().Be(NotificationUrgency.Info);
    }

    [Fact]
    public void ScheduleAndCondenser_ForAnEventWithNoScheduledTime_ReadInfo()
    {
        var instance = Run(TrackerMode.Event, null, (NotificationUrgency.Urgent, -24)) with { ScheduledAt = null };

        TrackerSchedule.UrgencyAt(instance, Reference).Should().Be(NotificationUrgency.Info);
        Condense(instance, Reference).Urgency.Should().Be(NotificationUrgency.Info);
    }

    private static IEnumerable<(int Offset, NotificationUrgency Expected)> Around(NotificationUrgency fired) =>
    [
        (-1, NotificationUrgency.Info),
        (0, fired),
        (1, fired),
    ];

    private static V4TrackerStatus Condense(TrackerInstance instance, DateTime now) =>
        TrackerStatusCondenser.Condense([instance], now).Single();

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
            StartedAt = Reference,
            ScheduledAt = mode == TrackerMode.Event ? Reference : null,
        };
}
