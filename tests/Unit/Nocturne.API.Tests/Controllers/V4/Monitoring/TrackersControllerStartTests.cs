using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>
/// Pins that a manual start replaces the running run of a Duration tracker, as a device-event start
/// does (<see cref="TrackerSuccession"/>), and how a definition's restart triggers are accepted.
/// </summary>
[Trait("Category", "Unit")]
public class TrackersControllerStartTests
{
    private const string OwnerId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private const string AdminId = "33333333-3333-3333-3333-333333333333";

    private readonly Mock<ITrackerRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IDeviceEventRepository> _deviceEvents = new();
    private readonly List<DeviceEvent> _recorded = [];

    public TrackersControllerStartTests()
    {
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));
        _deviceEvents
            .Setup(r => r.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((DeviceEvent e, WriteOrigin _, CancellationToken _) => _recorded.Add(e))
            .ReturnsAsync((DeviceEvent e, WriteOrigin _, CancellationToken _) => e);
    }

    private TrackersController CreateController(string subjectId = OwnerId, bool writesDevices = true)
    {
        var controller = new TrackersController(
            _repository.Object,
            _broadcast.Object,
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<IAlertRepository>(),
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IAlertAcknowledgementService>(),
            TrackerEventRecorderStub.Create(_deviceEvents.Object),
            Mock.Of<ILogger<TrackersController>>(),
            new FakeTimeProvider(Now)
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.Parse(subjectId),
        };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(
            writesDevices ? [Scope.AlertsReadWrite, Scope.DevicesReadWrite] : [Scope.AlertsReadWrite]);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private TrackerDefinitionEntity ArrangeDefinition(TrackerMode mode, params TrackerInstanceEntity[] running)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = OwnerId,
            Name = "Sensor",
            Mode = mode,
            LifespanHours = mode == TrackerMode.Duration ? 240 : null,
        };
        foreach (var instance in running)
        {
            instance.DefinitionId = definition.Id;
            instance.Definition = definition;
        }

        _repository
            .Setup(r => r.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(running.ToList());
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                It.IsAny<Guid>(), It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CompletionReason _, string? _, string? _, DateTime? _, CancellationToken _) =>
                running.First(i => i.Id == id));
        _repository
            .Setup(r => r.StartInstanceAsync(
                definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string userId, string? _, string? _, DateTime? startedAt, DateTime? scheduledAt, CancellationToken _) =>
                new TrackerInstanceEntity
                {
                    Id = Guid.NewGuid(),
                    DefinitionId = definition.Id,
                    Definition = definition,
                    CreatedBy = userId,
                    StartedAt = startedAt ?? Now,
                    ScheduledAt = scheduledAt,
                });
        return definition;
    }

    private static TrackerInstanceEntity Running(DateTime startedAt) =>
        new() { Id = Guid.NewGuid(), CreatedBy = OwnerId, StartedAt = startedAt };

    [Fact]
    public async Task StartInstance_CompletesTheRunningRunAtTheNewStart()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, old);

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _repository.Verify(r => r.CompleteInstanceAsync(
            old.Id, CompletionReason.ReplacedEarly, null, null, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartInstance_WithNoStartTime_StartsAtTheInjectedClocksTime()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);

        await CreateController().StartInstance(new StartTrackerInstanceRequest { DefinitionId = definition.Id });

        _repository.Verify(r => r.StartInstanceAsync(
            definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), Now,
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A member who neither created the tracker nor started its run.</summary>
    private const string MemberId = "22222222-2222-2222-2222-222222222222";

    private List<TrackerRealtimeAction> RecordBroadcasts()
    {
        var sent = new List<TrackerRealtimeAction>();
        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(It.IsAny<TrackerRealtimeAction>(), It.IsAny<TrackerInstanceDto>()))
            .Callback((TrackerRealtimeAction action, TrackerInstanceDto _) => sent.Add(action))
            .Returns(Task.CompletedTask);
        return sent;
    }

    [Fact]
    public async Task StartInstance_ByAnotherMember_ReplacesTheRunAndBroadcastsBoth()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, old);
        var sent = RecordBroadcasts();

        var result = await CreateController(MemberId).StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        sent.Should().Equal(TrackerRealtimeAction.Complete, TrackerRealtimeAction.Create);
        _repository.Verify(r => r.StartInstanceAsync(
            definition.Id, MemberId, It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private TrackerInstanceEntity ArrangeAdminStartedRun()
    {
        var run = new TrackerInstanceEntity { Id = Guid.NewGuid(), CreatedBy = AdminId, StartedAt = Now.AddDays(-1) };
        var definition = ArrangeDefinition(TrackerMode.Duration, run);
        _repository
            .Setup(r => r.GetInstanceByIdAsync(run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                run.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(run);
        run.Definition = definition;
        return run;
    }

    [Fact]
    public async Task CompleteInstance_ByAMemberWhoNeitherCreatedNorStartedIt_CompletesAndBroadcasts()
    {
        var run = ArrangeAdminStartedRun();
        var sent = RecordBroadcasts();

        var result = await CreateController(MemberId).CompleteInstance(
            run.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        result.Result.Should().BeOfType<OkObjectResult>();
        sent.Should().Equal(TrackerRealtimeAction.Complete);
    }

    [Fact]
    public async Task DeleteInstance_ByAMemberWhoNeitherCreatedNorStartedIt_DeletesAndBroadcasts()
    {
        var run = ArrangeAdminStartedRun();
        var sent = RecordBroadcasts();

        var result = await CreateController(MemberId).DeleteInstance(run.Id);

        result.Should().BeOfType<NoContentResult>();
        _repository.Verify(r => r.DeleteInstanceAsync(run.Id, It.IsAny<CancellationToken>()), Times.Once);
        sent.Should().Equal(TrackerRealtimeAction.Delete);
    }

    [Fact]
    public async Task StartInstance_WhenANewerRunIsRunning_ConflictsAndChangesNothing()
    {
        var newer = Running(Now);
        var definition = ArrangeDefinition(TrackerMode.Duration, newer);

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now.AddDays(-1) });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _repository.Verify(r => r.CompleteInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.StartInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartInstance_ForAnEventTracker_LeavesOtherBookingsRunning()
    {
        var booked = Running(Now.AddDays(-1));
        var definition = ArrangeDefinition(TrackerMode.Event, booked);

        var result = await CreateController().StartInstance(new StartTrackerInstanceRequest
        {
            DefinitionId = definition.Id,
            ScheduledAt = Now.AddDays(30),
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _repository.Verify(r => r.CompleteInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartInstance_WhenAnotherWriterCompletesTheRunningRunFirst_ConflictsAndStartsNothing()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, old);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                old.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerInstanceEntity?)null);

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _repository.Verify(r => r.StartInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteInstance_WhenAnotherWriterCompletesItFirst_ReportsItAlreadyCompleted()
    {
        var running = Running(Now.AddDays(-3));
        ArrangeDefinition(TrackerMode.Duration, running);
        _repository
            .Setup(r => r.GetInstanceByIdAsync(running.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(running);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                running.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerInstanceEntity?)null);

        var result = await CreateController().CompleteInstance(
            running.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ApplyPreset_CreatedByAnotherMember_StartsARunRecordedAsTheCaller()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        var preset = new TrackerPresetEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = MemberId,
            DefinitionId = definition.Id,
            Definition = definition,
        };
        _repository
            .Setup(r => r.GetPresetByIdAsync(preset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preset);

        var result = await CreateController().ApplyPreset(preset.Id);

        result.Result.Should().BeOfType<OkObjectResult>();
        _repository.Verify(r => r.StartInstanceAsync(
            definition.Id, OwnerId, It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDefinition_CanonicalisesTriggersAndDropsBlanks()
    {
        TrackerDefinitionEntity? persisted = null;
        _repository
            .Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinitionEntity, CancellationToken>((e, _) => persisted = e)
            .ReturnsAsync((TrackerDefinitionEntity e, CancellationToken _) => e);

        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            TriggerEventTypes = ["sensor start", "", "Sensor Start"],
            TriggerNotesContains = "  ",
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted!.TriggerEventTypes.Should().Be("[\"Sensor Start\"]");
        persisted.TriggerNotesContains.Should().BeNull();
    }

    [Fact]
    public async Task CreateDefinition_RejectsAnEventNoTrackerCanRestartOn()
    {
        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            TriggerEventTypes = ["Pump Suspend"],
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repository.Verify(r => r.CreateDefinitionAsync(
            It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private TrackerDefinitionEntity ArrangeStored(string triggers, string? notes)
    {
        var existing = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            CreatedBy = OwnerId,
            Name = "Sensor",
            TriggerEventTypes = triggers,
            TriggerNotesContains = notes,
        };
        _repository
            .Setup(r => r.GetDefinitionByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repository
            .Setup(r => r.UpdateDefinitionAsync(existing.Id, It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TrackerDefinitionEntity e, CancellationToken _) => e);
        return existing;
    }

    // The editor posts one blank trigger for "none", and a cleared notes box arrives as no field at
    // all because the form layer drops empty strings.
    [Fact]
    public async Task UpdateDefinition_FromAClearedEditor_ClearsTriggersAndTheNotesFilter()
    {
        var existing = ArrangeStored("[\"Sensor Start\"]", "left");

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = [""],
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        existing.TriggerEventTypes.Should().Be("[]");
        existing.TriggerNotesContains.Should().BeNull();
    }


    [Fact]
    public async Task UpdateDefinition_ClearingOnlyTheNotesFilter_KeepsTheTriggers()
    {
        var existing = ArrangeStored("[\"Site Change\"]", "left");

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = ["Site Change"],
        });

        existing.TriggerEventTypes.Should().Be("[\"Site Change\"]");
        existing.TriggerNotesContains.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDefinition_WithNoTriggerList_KeepsTriggersAndNotes()
    {
        var existing = ArrangeStored("[\"Site Change\"]", "left");

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest { Name = "Site" });

        existing.TriggerEventTypes.Should().Be("[\"Site Change\"]");
        existing.TriggerNotesContains.Should().Be("left");
    }

    [Fact]
    public async Task UpdateDefinition_RejectsATriggerNotInTheList()
    {
        var existing = ArrangeStored("[\"Sensor Start\"]", null);

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = ["Pump Suspend", "Sensor Start"],
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void Dto_ListsOnlyTriggersInTheList()
    {
        var dto = TrackerDefinitionDto.FromEntity(new TrackerDefinitionEntity
        {
            TriggerEventTypes = "[\"Pump Suspend\",\"sensor start\"]",
        });

        dto.TriggerEventTypes.Should().Equal("Sensor Start");
    }

    [Fact]
    public async Task StartInstance_WithAStartEvent_RecordsItLinkedToTheNewRun()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        definition.StartEventType = "Sensor Start";
        string? linked = null;
        _repository
            .Setup(r => r.StartInstanceAsync(
                definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, string _, string? _, string? treatment, DateTime? _, DateTime? _, CancellationToken _) =>
                linked = treatment)
            .ReturnsAsync(new TrackerInstanceEntity { Id = Guid.NewGuid(), DefinitionId = definition.Id, StartedAt = Now });

        await CreateController().StartInstance(new StartTrackerInstanceRequest
        {
            DefinitionId = definition.Id,
            StartedAt = Now,
            StartNotes = "Left arm",
        });

        var recorded = _recorded.Should().ContainSingle().Subject;
        recorded.EventType.Should().Be(DeviceEventType.SensorStart);
        recorded.Timestamp.Should().Be(Now);
        recorded.Notes.Should().Be("Left arm");
        recorded.App.Should().Be(TrackerEventRecorder.App);
        linked.Should().Be(recorded.Id.ToString());
        _deviceEvents.Verify(r => r.CreateAsync(It.IsAny<DeviceEvent>(), WriteOrigin.Live, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task StartInstance_WithoutTheDeviceWriteScope_RecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        definition.StartEventType = "Sensor Start";

        await CreateController(writesDevices: false).StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        _recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task StartInstance_FromATreatmentOfItsOwn_RecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        definition.StartEventType = "Sensor Start";

        await CreateController().StartInstance(new StartTrackerInstanceRequest
        {
            DefinitionId = definition.Id,
            StartedAt = Now,
            StartTreatmentId = "treatment-1",
        });

        _recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task StartInstance_WhenItConflicts_RecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration, Running(Now));
        definition.StartEventType = "Sensor Start";

        await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now.AddDays(-1) });

        _recorded.Should().BeEmpty();
    }

    // A definition saved before the event type was checked may hold a treatment type that is no
    // device event.
    [Fact]
    public async Task StartInstance_WithAStoredTypeThatIsNoDeviceEvent_RecordsNothing()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        definition.StartEventType = "Note";

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteInstance_WithACompletionEvent_RecordsItLinkedToTheRun()
    {
        var run = ArrangeAdminStartedRun();
        run.Definition.CompletionEventType = "sensor stop";

        await CreateController().CompleteInstance(run.Id, new CompleteTrackerInstanceRequest
        {
            Reason = CompletionReason.Completed,
            CompletionNotes = "Fell off",
        });

        var recorded = _recorded.Should().ContainSingle().Subject;
        recorded.EventType.Should().Be(DeviceEventType.SensorStop);
        recorded.Timestamp.Should().Be(Now);
        recorded.Notes.Should().Be("Fell off");
        _repository.Verify(r => r.CompleteInstanceAsync(
            run.Id, CompletionReason.Completed, "Fell off", recorded.Id.ToString(), Now, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task CreateDefinition_CanonicalisesTheEventTypes()
    {
        TrackerDefinitionEntity? persisted = null;
        _repository
            .Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinitionEntity, CancellationToken>((e, _) => persisted = e)
            .ReturnsAsync((TrackerDefinitionEntity e, CancellationToken _) => e);

        await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            StartEventType = "sensor start",
            CompletionEventType = " ",
        });

        persisted!.StartEventType.Should().Be("Sensor Start");
        persisted.CompletionEventType.Should().BeNull();
    }

    [Fact]
    public async Task CreateDefinition_RejectsAnEventTypeThatIsNoDeviceEvent()
    {
        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            StartEventType = "Note",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateDefinition_ABlankEventTypeClearsIt_AndAnAbsentOneKeepsIt()
    {
        var existing = ArrangeStored("[]", null);
        existing.StartEventType = "Sensor Start";
        existing.CompletionEventType = "Sensor Stop";

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            StartEventType = "",
        });

        existing.StartEventType.Should().BeNull();
        existing.CompletionEventType.Should().Be("Sensor Stop");
    }

    // Deleting the recorded event undoes the run it started and resumes the one it replaced, which
    // is found by the replaced run's completing treatment.
    [Fact]
    public async Task StartInstance_WithAStartEvent_CompletesTheReplacedRunWithTheSameEvent()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, old);
        definition.StartEventType = "Sensor Start";

        await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        var recorded = _recorded.Should().ContainSingle().Subject;
        _repository.Verify(r => r.CompleteInstanceAsync(
            old.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), recorded.Id.ToString(),
            Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDefinition_RejectsACompletionEventItRestartsOn()
    {
        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Site",
            TriggerEventTypes = ["Site Change"],
            CompletionEventType = "site change",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateDefinition_RejectsACompletionEventAStoredTriggerMatches()
    {
        var existing = ArrangeStored("[\"Site Change\"]", null);

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            CompletionEventType = "Site Change",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        existing.CompletionEventType.Should().BeNull();
    }

    [Fact]
    public void Dto_DropsAStoredEventTypeThatIsNoDeviceEvent()
    {
        var dto = TrackerDefinitionDto.FromEntity(new TrackerDefinitionEntity
        {
            StartEventType = "Note",
            CompletionEventType = "sensor stop",
        });

        dto.StartEventType.Should().BeNull();
        dto.CompletionEventType.Should().Be("Sensor Stop");
    }
}
