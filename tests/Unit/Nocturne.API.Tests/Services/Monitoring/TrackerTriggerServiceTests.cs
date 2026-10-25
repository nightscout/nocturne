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
                    CreatedBy = userId,
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

        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));

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
            CreatedBy = userId,
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
            CreatedBy = definition.CreatedBy,
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
    public async Task AStoredTriggerOutsideTheTriggerableList_DoesNotAdvanceTheTracker()
    {
        GivenDefinition(triggerEventTypes: ["Pump Suspend", "Sensor Start"]);

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc), DeviceEventType.PumpSuspend)]);

        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task ATriggerableStoredTrigger_StillAdvancesAlongsideAnIgnoredOne()
    {
        GivenDefinition(triggerEventTypes: ["Pump Suspend", "Sensor Start"]);

        await _sut.OnCreatedAsync([Event(new DateTime(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc), DeviceEventType.SensorStart)]);

        _started.Should().ContainSingle();
    }

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
        _started.Should().ContainSingle().Which.CreatedBy.Should().Be("someone-else");
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

        _broadcast.Verify(
            b => b.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Complete,
                It.IsAny<TrackerInstanceDto>()
            ),
            Times.Once
        );
        _broadcast.Verify(
            b => b.BroadcastTrackerUpdateAsync(
                TrackerRealtimeAction.Create,
                It.IsAny<TrackerInstanceDto>()
            ),
            Times.Once
        );
    }

    private readonly List<(Guid DefinitionId, string StartTreatmentId)> _reverts = [];

    /// <summary>
    /// Wires the delete path over <paramref name="runs"/>: each run's start can be reverted once,
    /// reopening the runs in <paramref name="replaced"/> keyed by the reverted run.
    /// </summary>
    private void GivenRunsStartedByDeletes(
        IReadOnlyList<TrackerInstanceEntity> runs,
        IReadOnlyDictionary<Guid, TrackerInstanceEntity[]>? replaced = null)
    {
        var running = runs.Where(r => r.CompletedAt == null).ToList();
        _repository
            .Setup(r => r.GetRunningStartedByAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) =>
                running.Where(r => ids.Contains(r.StartTreatmentId!)).ToList());
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerStartRevert?>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerStartRevert?, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerStartRevert?>> work,
                Func<ITrackerRepository, TrackerStartRevert?, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));
        _repository
            .Setup(r => r.RevertStartAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid definitionId, string startTreatmentId, CancellationToken _) =>
            {
                _reverts.Add((definitionId, startTreatmentId));
                var run = running.FirstOrDefault(r => r.DefinitionId == definitionId && r.StartTreatmentId == startTreatmentId);
                if (run is null)
                    return null;
                running.Remove(run);
                var reopened = replaced?.GetValueOrDefault(run.Id) ?? [];
                foreach (var resumed in reopened)
                {
                    resumed.CompletedAt = null;
                    running.Add(resumed);
                }
                return new TrackerStartRevert(run, reopened);
            });
    }

    private static TrackerInstanceEntity Run(
        TrackerDefinitionEntity definition, DeviceEvent? startedBy, DateTime startedAt, DateTime? completedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            CreatedBy = definition.CreatedBy,
            Definition = definition,
            StartTreatmentId = startedBy?.Id.ToString(),
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

    private void VerifyBroadcast(TrackerRealtimeAction action, TrackerInstanceEntity instance, Times times) =>
        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync(
            action, It.Is<TrackerInstanceDto>(d => d.Id == instance.Id)), times);

    private static readonly DateTime T0 = new(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc);

    [Fact]
    public async Task DeletedChange_UndoesTheRunItStartedAndBroadcastsTheResumedOne()
    {
        var definition = GivenDefinition();
        var deviceEvent = Event(T0);
        var previous = Run(definition, null, T0.AddDays(-3), completedAt: T0);
        var started = Run(definition, deviceEvent, T0);
        GivenRunsStartedByDeletes([started], new Dictionary<Guid, TrackerInstanceEntity[]> { [started.Id] = [previous] });

        await _sut.OnDeletedAsync([deviceEvent]);

        _reverts.Should().Equal((definition.Id, deviceEvent.Id.ToString()));
        VerifyBroadcast(TrackerRealtimeAction.Delete, started, Times.Once());
        VerifyBroadcast(TrackerRealtimeAction.Reopen, previous, Times.Once());
    }

    [Fact]
    public async Task DeletedChange_ThatStartedNothingStillRunning_ChangesNothing()
    {
        GivenDefinition();
        GivenRunsStartedByDeletes([]);

        await _sut.OnDeletedAsync([Event(T0)]);

        _reverts.Should().BeEmpty();
        _broadcast.VerifyNoOtherCalls();
    }

    /// <summary>The run is found by the event that started it, whatever its definition follows now.</summary>
    [Fact]
    public async Task DeletedChange_UndoesItsRunEvenWhenTheDefinitionNoLongerFollowsIt()
    {
        var definition = GivenDefinition(triggerEventTypes: ["Sensor Change"]);
        var deviceEvent = Event(T0);
        GivenRunsStartedByDeletes([Run(definition, deviceEvent, T0)]);

        await _sut.OnDeletedAsync([deviceEvent]);

        _reverts.Should().ContainSingle();
    }

    /// <summary>
    /// Deleting two chained changes at once: only once the newer one's run is undone is the older
    /// one's run running again, and then it is undone too.
    /// </summary>
    [Fact]
    public async Task DeletedChain_IsUndoneBackToTheRunBeforeIt()
    {
        var definition = GivenDefinition();
        var older = Event(T0.AddDays(-2));
        var newer = Event(T0);
        var first = Run(definition, null, T0.AddDays(-5), completedAt: T0.AddDays(-2));
        var second = Run(definition, older, T0.AddDays(-2), completedAt: T0);
        var third = Run(definition, newer, T0);
        GivenRunsStartedByDeletes(
            [first, second, third],
            new Dictionary<Guid, TrackerInstanceEntity[]> { [third.Id] = [second], [second.Id] = [first] });

        await _sut.OnDeletedAsync([older, newer]);

        _reverts.Select(r => r.StartTreatmentId).Should().Equal(newer.Id.ToString(), older.Id.ToString());
        VerifyBroadcast(TrackerRealtimeAction.Reopen, first, Times.Once());
    }

    [Fact]
    public async Task Replay_ReplacesTheRunningRun()
    {
        var definition = GivenDefinition();
        var running = GivenActiveInstance(definition, T0.AddDays(-3));

        await _sut.OnReplayedAsync([Event(T0)]);

        _completed.Should().ContainSingle().Which.InstanceId.Should().Be(running.Id);
        _started.Should().ContainSingle();
    }

    /// <summary>A run the user ended by hand after the event stays ended.</summary>
    [Fact]
    public async Task Replay_StartsNothingOnATrackerEndedSinceTheEvent()
    {
        var definition = GivenDefinition();
        _repository
            .Setup(r => r.HasRunEndedAfterAsync(definition.Id, T0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.OnReplayedAsync([Event(T0)]);

        _started.Should().BeEmpty();
    }

    /// <summary>
    /// The undone run was the tracker's first, so nothing reopened: the replayed change is the one
    /// that should have started it.
    /// </summary>
    [Fact]
    public async Task Replay_StartsTheFirstRunOfAnIdleTracker()
    {
        var definition = GivenDefinition();
        _repository
            .Setup(r => r.HasRunEndedAfterAsync(definition.Id, T0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.OnReplayedAsync([Event(T0)]);

        _started.Should().ContainSingle().Which.StartedAt.Should().Be(T0);
    }

    [Fact]
    public async Task Create_StartsAnIdleTrackerWhateverEndedBefore()
    {
        var definition = GivenDefinition();
        _repository
            .Setup(r => r.HasRunEndedAfterAsync(definition.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.OnCreatedAsync([Event(T0)]);

        _started.Should().ContainSingle();
    }

    [Fact]
    public async Task Followed_IsTheEventsThatStartedARunStillGoing()
    {
        var definition = GivenDefinition();
        var following = Event(T0);
        var other = Event(T0.AddDays(-3));
        _repository
            .Setup(r => r.GetRunningStartedByAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Run(definition, following, T0)]);

        var followed = await _sut.GetFollowedAsync([following.Id, other.Id]);

        followed.Should().BeEquivalentTo([following.Id]);
    }
}

