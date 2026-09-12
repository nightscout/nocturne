using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>
/// Covers the device-event reaction that advances tracker instances. The behaviour these pin down is
/// what the connector ingest path depends on: definitions are matched tenant-wide (a connector event
/// carries no user), re-published and backdated events must not rewind a tracker, and a batch must
/// chain in timestamp order.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerTriggerServiceTests
{
    private readonly Mock<ITrackerRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly TrackerTriggerService _sut;

    private readonly List<TrackerDefinitionEntity> _definitions = [];
    private readonly Dictionary<Guid, List<TrackerInstanceEntity>> _activeByDefinition = [];
    private readonly List<TrackerInstanceEntity> _started = [];
    private readonly List<(Guid InstanceId, CompletionReason Reason, DateTime? CompletedAt, string? CompleteTreatmentId)> _completed = [];

    public TrackerTriggerServiceTests()
    {
        _repository
            .Setup(r => r.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _definitions);

        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid definitionId, CancellationToken _) =>
                _activeByDefinition.TryGetValue(definitionId, out var list) ? list : []);

        _repository
            .Setup(r => r.StartInstanceAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid definitionId, string userId, string? startNotes, string? startTreatmentId,
                DateTime? startedAt, DateTime? scheduledAt, CancellationToken _) =>
            {
                var instance = new TrackerInstanceEntity
                {
                    Id = Guid.NewGuid(),
                    DefinitionId = definitionId,
                    UserId = userId,
                    StartNotes = startNotes,
                    StartTreatmentId = startTreatmentId,
                    StartedAt = startedAt ?? DateTime.UtcNow,
                    ScheduledAt = scheduledAt,
                };
                _started.Add(instance);
                // Mirror the repository: a started instance becomes the definition's active one, which is
                // what lets a multi-event batch chain through this mock.
                _activeByDefinition[definitionId] = [instance];
                return instance;
            });

        _repository
            .Setup(r => r.CompleteInstanceAsync(
                It.IsAny<Guid>(), It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid instanceId, CompletionReason reason, string? notes, string? completeTreatmentId,
                DateTime? completedAt, CancellationToken _) =>
            {
                _completed.Add((instanceId, reason, completedAt, completeTreatmentId));

                var instance = _activeByDefinition.Values
                    .SelectMany(list => list)
                    .First(i => i.Id == instanceId);

                instance.CompletedAt = completedAt ?? DateTime.UtcNow;
                instance.CompletionReason = reason;
                _activeByDefinition[instance.DefinitionId] =
                    [.. _activeByDefinition[instance.DefinitionId].Where(i => i.Id != instanceId)];
                return instance;
            });

        _sut = new TrackerTriggerService(
            _repository.Object,
            _broadcast.Object,
            NullLogger<TrackerTriggerService>.Instance);
    }

    private TrackerDefinitionEntity GivenDefinition(
        string[]? triggerEventTypes = null,
        int? lifespanHours = 72,
        TrackerMode mode = TrackerMode.Duration,
        string userId = "definition-owner",
        string? triggerNotesContains = null)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Infusion Site",
            UserId = userId,
            LifespanHours = lifespanHours,
            Mode = mode,
            TriggerNotesContains = triggerNotesContains,
            TriggerEventTypes = JsonSerializer.Serialize(triggerEventTypes ?? ["Site Change"]),
        };
        _definitions.Add(definition);
        return definition;
    }

    private TrackerInstanceEntity GivenActiveInstance(
        TrackerDefinitionEntity definition, DateTime startedAt, string? startTreatmentId = null)
    {
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            UserId = definition.UserId,
            StartedAt = startedAt,
            StartTreatmentId = startTreatmentId,
        };
        _activeByDefinition[definition.Id] = [instance];
        return instance;
    }

    private static DeviceEvent Event(
        DateTime timestamp,
        DeviceEventType eventType = DeviceEventType.SiteChange,
        string? notes = null) =>
        new() { Id = Guid.NewGuid(), Timestamp = timestamp, EventType = eventType, Notes = notes };

    [Fact]
    public async Task SiteChange_StartsAnInstance_WhenNoneIsRunning()
    {
        var definition = GivenDefinition();
        var deviceEvent = Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));

        await _sut.OnCreatedAsync([deviceEvent]);

        _started.Should().ContainSingle();
        _started[0].DefinitionId.Should().Be(definition.Id);
        _started[0].StartedAt.Should().Be(deviceEvent.Timestamp);
        _started[0].StartTreatmentId.Should().Be(deviceEvent.Id.ToString());
        _completed.Should().BeEmpty();
    }

    [Fact]
    public async Task SiteChange_CompletesTheRunningInstance_BeforeStartingTheNext()
    {
        var definition = GivenDefinition(lifespanHours: 72);
        var running = GivenActiveInstance(definition, new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc));
        var deviceEvent = Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));

        await _sut.OnCreatedAsync([deviceEvent]);

        _completed.Should().ContainSingle();
        _completed[0].InstanceId.Should().Be(running.Id);
        _completed[0].CompletedAt.Should().Be(deviceEvent.Timestamp);
        _completed[0].CompleteTreatmentId.Should().Be(deviceEvent.Id.ToString());
        _started.Should().ContainSingle();
        _started[0].StartedAt.Should().Be(deviceEvent.Timestamp);
    }

    [Fact]
    public async Task RunningPastItsLifespan_CompletesAsExpired()
    {
        var definition = GivenDefinition(lifespanHours: 72);
        GivenActiveInstance(definition, new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc));

        // 99.5 hours later — past the 72h term.
        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        _completed.Should().ContainSingle().Which.Reason.Should().Be(CompletionReason.Expired);
    }

    [Fact]
    public async Task SwappedBeforeItsLifespan_CompletesAsReplacedEarly()
    {
        var definition = GivenDefinition(lifespanHours: 72);
        GivenActiveInstance(definition, new DateTime(2026, 9, 1, 4, 14, 50, DateTimeKind.Utc));

        // 50.3 hours later — short of the 72h term.
        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 3, 6, 32, 50, DateTimeKind.Utc))]);

        _completed.Should().ContainSingle().Which.Reason.Should().Be(CompletionReason.ReplacedEarly);
    }

    [Fact]
    public async Task DefinitionWithoutALifespan_CompletesAsPlainCompleted()
    {
        var definition = GivenDefinition(lifespanHours: null);
        GivenActiveInstance(definition, new DateTime(2026, 9, 1, 4, 14, 50, DateTimeKind.Utc));

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 3, 6, 32, 50, DateTimeKind.Utc))]);

        _completed.Should().ContainSingle().Which.Reason.Should().Be(CompletionReason.Completed);
    }

    [Fact]
    public async Task NewInstance_IsOwnedByTheDefinitionOwner()
    {
        GivenDefinition(userId: "someone-else");

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        // A connector-ingested event carries no user, so ownership can only come from the definition.
        _started.Should().ContainSingle().Which.UserId.Should().Be("someone-else");
    }

    [Fact]
    public async Task EventOlderThanTheRunningInstance_IsIgnored()
    {
        var definition = GivenDefinition();
        GivenActiveInstance(definition, new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));

        // A connector backfilling its window re-delivers older changes; acting on one would rewind
        // the tracker to a site that has already been replaced.
        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc))]);

        _started.Should().BeEmpty();
        _completed.Should().BeEmpty();
    }

    [Fact]
    public async Task EventAlreadyStartedAnInstance_IsIgnoredOnRedelivery()
    {
        var definition = GivenDefinition();
        var deviceEvent = Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));
        GivenActiveInstance(definition, deviceEvent.Timestamp, startTreatmentId: deviceEvent.Id.ToString());

        await _sut.OnCreatedAsync([deviceEvent]);

        _started.Should().BeEmpty();
        _completed.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchOfChanges_ChainsInTimestampOrder()
    {
        var definition = GivenDefinition(lifespanHours: 72);
        var first = Event(new DateTime(2026, 9, 3, 6, 32, 50, DateTimeKind.Utc));
        var second = Event(new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc));
        var third = Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));

        // Delivered newest-first, as a connector page arrives.
        await _sut.OnCreatedAsync([third, first, second]);

        _started.Select(i => i.StartedAt).Should().Equal(first.Timestamp, second.Timestamp, third.Timestamp);
        _completed.Select(c => c.CompletedAt).Should().Equal(second.Timestamp, third.Timestamp);
        _activeByDefinition[definition.Id].Should().ContainSingle()
            .Which.StartedAt.Should().Be(third.Timestamp);
    }

    [Fact]
    public async Task EventTypeTheDefinitionDoesNotTrigger_On_IsIgnored()
    {
        GivenDefinition(triggerEventTypes: ["Site Change"]);

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc), DeviceEventType.SensorStart)]);

        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task EventModeDefinition_IsNotTriggered()
    {
        // Event-mode instances need a ScheduledAt that a device event cannot supply.
        GivenDefinition(mode: TrackerMode.Event);

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerNotesFilter_AdmitsOnlyMatchingEvents()
    {
        GivenDefinition(triggerNotesContains: "left arm");

        await _sut.OnCreatedAsync([
            Event(new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc), notes: "right thigh"),
            Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc), notes: "Left Arm, no bleeding"),
        ]);

        _started.Should().ContainSingle()
            .Which.StartedAt.Should().Be(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SeveralDefinitionsTriggeringOnTheSameEvent_AllAdvance()
    {
        var site = GivenDefinition(triggerEventTypes: ["Site Change"], userId: "owner-a");
        var cannula = GivenDefinition(triggerEventTypes: ["Site Change"], userId: "owner-b");

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        _started.Select(i => i.DefinitionId).Should().BeEquivalentTo([site.Id, cannula.Id]);
    }

    [Fact]
    public async Task TriggerListNamingSomethingThatIsNotADeviceEvent_IsDropped()
    {
        GivenDefinition(triggerEventTypes: ["Meal Bolus", "Correction Bolus"]);

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedTriggerList_IsIgnoredWithoutThrowing()
    {
        var definition = GivenDefinition();
        definition.TriggerEventTypes = "{not json";

        var act = async () => await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        await act.Should().NotThrowAsync();
        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task NoDeviceEvents_TouchesNothing()
    {
        GivenDefinition();

        await _sut.OnCreatedAsync([]);

        _repository.Verify(r => r.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _started.Should().BeEmpty();
    }

    [Fact]
    public void TriggerIsRegisteredAsTheDeviceEventReactor()
    {
        // Nothing else resolves the trigger, so this registration is the whole mechanism by which it
        // runs; unregistered, trackers stop advancing with no other symptom.
        var services = new ServiceCollection().AddDomainServices();

        var descriptor = services.Should()
            .ContainSingle(d => d.ServiceType == typeof(IDeviceEventReactor)).Subject;

        descriptor.ImplementationType.Should().Be<TrackerTriggerService>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task AdvancingATracker_BroadcastsTheCompletionAndTheNewInstance()
    {
        var definition = GivenDefinition();
        GivenActiveInstance(definition, new DateTime(2026, 9, 7, 7, 11, 50, DateTimeKind.Utc));

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc))]);

        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync("complete", It.IsAny<object>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync("create", It.IsAny<object>()), Times.Once);
    }
}
