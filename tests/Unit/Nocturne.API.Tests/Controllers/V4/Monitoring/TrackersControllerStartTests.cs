using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
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
    private readonly List<DeviceEvent> _written = [];

    public TrackersControllerStartTests()
    {
        _deviceEvents
            .Setup(d => d.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceEvent, WriteOrigin, CancellationToken>((e, _, _) => _written.Add(e))
            .ReturnsAsync((DeviceEvent e, WriteOrigin _, CancellationToken _) => e);
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));
    }

    private TrackersController CreateController(string subjectId = OwnerId)
    {
        var time = new FakeTimeProvider(Now);
        var controller = new TrackersController(
            new TrackerService(
                _repository.Object,
                _broadcast.Object,
                Mock.Of<ITrackerAlertRuleSyncService>(),
                Mock.Of<IAlertRepository>(),
                Mock.Of<ITenantAccessor>(),
                Mock.Of<IAlertAcknowledgementService>(),
                new TrackerDeviceEventWriter(
                    _deviceEvents.Object,
                    Mock.Of<IPatientDeviceStamper>(),
                    NullLogger<TrackerDeviceEventWriter>.Instance),
                time,
                Mock.Of<ILogger<TrackerService>>()),
            time
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.Parse(subjectId),
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>A run to arrange as running, before its definition is known.</summary>
    private sealed record Run(Guid Id, DateTime StartedAt, string CreatedBy = OwnerId);

    private static Run Running(DateTime startedAt, string createdBy = OwnerId) => new(Guid.NewGuid(), startedAt, createdBy);

    private static TrackerInstance Instance(TrackerDefinition definition, Run run) => new()
    {
        Id = run.Id,
        CreatedBy = run.CreatedBy,
        StartedAt = run.StartedAt,
        Definition = definition,
    };

    private TrackerDefinition ArrangeDefinition(
        TrackerMode mode,
        Run[]? running = null,
        string? startEventType = null,
        string? completionEventType = null)
    {
        var definition = TrackerDefinition.Restore(
            Guid.NewGuid(),
            "Sensor",
            mode: mode,
            createdBy: OwnerId,
            lifespanHours: mode == TrackerMode.Duration ? 240 : null,
            startEventType: startEventType,
            completionEventType: completionEventType);
        var instances = (running ?? []).Select(run => Instance(definition, run)).ToList();

        _repository
            .Setup(r => r.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(instances);
        foreach (var instance in instances)
        {
            _repository
                .Setup(r => r.GetInstanceByIdAsync(instance.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(instance);
            _repository
                .Setup(r => r.CompleteInstanceAsync(
                    instance.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(instance);
        }
        _repository
            .Setup(r => r.StartInstanceAsync(
                definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string userId, string? _, string? _, DateTime? startedAt, DateTime? scheduledAt, CancellationToken _) =>
                new TrackerInstance
                {
                    Id = Guid.NewGuid(),
                    Definition = definition,
                    CreatedBy = userId,
                    StartedAt = startedAt ?? Now,
                    ScheduledAt = scheduledAt,
                });
        return definition;
    }

    [Fact]
    public async Task StartInstance_CompletesTheRunningRunAtTheNewStart()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, [old]);

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
        var definition = ArrangeDefinition(TrackerMode.Duration, [old]);
        var sent = RecordBroadcasts();

        var result = await CreateController(MemberId).StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        sent.Should().Equal(TrackerRealtimeAction.Complete, TrackerRealtimeAction.Create);
        _repository.Verify(r => r.StartInstanceAsync(
            definition.Id, MemberId, It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private Run ArrangeAdminStartedRun()
    {
        var run = Running(Now.AddDays(-1), AdminId);
        ArrangeDefinition(TrackerMode.Duration, [run]);
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
        var definition = ArrangeDefinition(TrackerMode.Duration, [Running(Now)]);

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
        var definition = ArrangeDefinition(TrackerMode.Event, [Running(Now.AddDays(-1))]);

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
        var definition = ArrangeDefinition(TrackerMode.Duration, [old]);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                old.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerInstance?)null);

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
        ArrangeDefinition(TrackerMode.Duration, [running]);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                running.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerInstance?)null);

        var result = await CreateController().CompleteInstance(
            running.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ApplyPreset_CreatedByAnotherMember_StartsARunRecordedAsTheCaller()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        var preset = new TrackerPreset
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

    private Func<TrackerDefinition?> CapturePersisted()
    {
        TrackerDefinition? persisted = null;
        _repository
            .Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinition, CancellationToken>((d, _) => persisted = d)
            .ReturnsAsync((TrackerDefinition d, CancellationToken _) => d);
        return () => persisted;
    }

    [Fact]
    public async Task CreateDefinition_CanonicalisesTriggersAndDropsBlanks()
    {
        var persisted = CapturePersisted();

        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            TriggerEventTypes = ["sensor start", "", "Sensor Start"],
            TriggerNotesContains = "  ",
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted()!.TriggerEventTypes.Should().Equal("Sensor Start");
        persisted()!.TriggerNotesContains.Should().BeNull();
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
            It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A stored definition, and what the update saves of it.</summary>
    private (TrackerDefinition Existing, Func<TrackerDefinition?> Saved) ArrangeStored(
        string[] triggers, string? notes, string? startEventType = null, string? completionEventType = null)
    {
        var existing = TrackerDefinition.Restore(
            Guid.NewGuid(),
            "Sensor",
            createdBy: OwnerId,
            triggerEventTypes: triggers,
            triggerNotesContains: notes,
            startEventType: startEventType,
            completionEventType: completionEventType);
        TrackerDefinition? saved = null;
        _repository
            .Setup(r => r.GetDefinitionByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repository
            .Setup(r => r.UpdateDefinitionAsync(It.Is<TrackerDefinition>(d => d.Id == existing.Id), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinition, CancellationToken>((d, _) => saved = d)
            .ReturnsAsync((TrackerDefinition d, CancellationToken _) => d);
        return (existing, () => saved);
    }

    // The editor posts one blank trigger for "none", and a cleared notes box arrives as no field at
    // all because the form layer drops empty strings.
    [Fact]
    public async Task UpdateDefinition_FromAClearedEditor_ClearsTriggersAndTheNotesFilter()
    {
        var (existing, saved) = ArrangeStored(["Sensor Start"], "left");

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = [""],
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        saved()!.TriggerEventTypes.Should().BeEmpty();
        saved()!.TriggerNotesContains.Should().BeNull();
    }


    [Fact]
    public async Task UpdateDefinition_ClearingOnlyTheNotesFilter_KeepsTheTriggers()
    {
        var (existing, saved) = ArrangeStored(["Site Change"], "left");

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = ["Site Change"],
        });

        saved()!.TriggerEventTypes.Should().Equal("Site Change");
        saved()!.TriggerNotesContains.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDefinition_WithNoTriggerList_KeepsTriggersAndNotes()
    {
        var (existing, saved) = ArrangeStored(["Site Change"], "left");

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest { Name = "Site" });

        saved()!.TriggerEventTypes.Should().Equal("Site Change");
        saved()!.TriggerNotesContains.Should().Be("left");
    }

    [Fact]
    public async Task UpdateDefinition_RejectsATriggerNotInTheList()
    {
        var (existing, _) = ArrangeStored(["Sensor Start"], null);

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
        var dto = TrackerDefinitionDto.From(TrackerDefinition.Restore(
            Guid.Empty, string.Empty, triggerEventTypes: ["Pump Suspend", "sensor start"]));

        dto.TriggerEventTypes.Should().Equal("Sensor Start");
    }

    [Fact]
    public async Task StartInstance_RecordsTheDefinitionsStartEventAsATrackerDeviceEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration, startEventType: "Sensor Start");
        var startedAt = Now.AddHours(-2);

        await CreateController().StartInstance(new StartTrackerInstanceRequest
        {
            DefinitionId = definition.Id,
            StartedAt = startedAt,
            StartNotes = "left arm",
        });

        var written = _written.Should().ContainSingle().Subject;
        written.EventType.Should().Be(DeviceEventType.SensorStart);
        written.Timestamp.Should().Be(startedAt);
        written.Notes.Should().Be("left arm");
        written.DataSource.Should().Be(DataSources.Tracker);
        _deviceEvents.Verify(d => d.CreateAsync(It.IsAny<DeviceEvent>(), WriteOrigin.Live, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task StartInstance_WithNoStartEventType_RecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);

        await CreateController().StartInstance(new StartTrackerInstanceRequest { DefinitionId = definition.Id });

        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task StartInstance_WithAStoredEventTypeThatIsNoDeviceEvent_StartsAndRecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration, startEventType: "Meal Bolus");

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task StartInstance_ThatLosesToANewerRun_RecordsNoEvent()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration, [Running(Now)], startEventType: "Sensor Start");

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now.AddHours(-1) });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteInstance_RecordsTheDefinitionsCompletionEventAtTheCompletionTime()
    {
        var run = Running(Now.AddDays(-9));
        var definition = ArrangeDefinition(TrackerMode.Duration, [run], completionEventType: "Sensor Stop");
        var completedAt = Now.AddHours(-1);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                run.Id, CompletionReason.Completed, "fell off", null, completedAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Instance(definition, run) with
            {
                CompletedAt = completedAt,
                CompletionReason = CompletionReason.Completed,
                CompletionNotes = "fell off",
            });

        await CreateController().CompleteInstance(run.Id, new CompleteTrackerInstanceRequest
        {
            Reason = CompletionReason.Completed,
            CompletionNotes = "fell off",
            CompletedAt = completedAt,
        });

        var written = _written.Should().ContainSingle().Subject;
        written.EventType.Should().Be(DeviceEventType.SensorStop);
        written.Timestamp.Should().Be(completedAt);
        written.Notes.Should().Be("fell off");
        written.DataSource.Should().Be(DataSources.Tracker);
    }

    [Fact]
    public async Task CreateDefinition_CanonicalisesItsStartAndCompletionEventTypes()
    {
        var persisted = CapturePersisted();

        await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            StartEventType = "sensor start",
            CompletionEventType = " ",
        });

        persisted()!.StartEventType.Should().Be("Sensor Start");
        persisted()!.CompletionEventType.Should().BeNull();
    }

    [Fact]
    public async Task CreateDefinition_RejectsAStartEventTypeThatIsNoDeviceEvent()
    {
        var result = await CreateController().CreateDefinition(new CreateTrackerDefinitionRequest
        {
            Name = "Sensor",
            StartEventType = "Meal Bolus",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Contain("StartEventType");
        _repository.Verify(r => r.CreateDefinitionAsync(
            It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateDefinition_RejectsACompletionEventTypeThatIsNoDeviceEvent()
    {
        var (existing, saved) = ArrangeStored([], null);

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            CompletionEventType = "Note",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Contain("CompletionEventType");
        saved().Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task UpdateDefinition_WithABlankEventType_ClearsBothEvents(string? blank)
    {
        var (existing, saved) = ArrangeStored([], null, startEventType: "Sensor Start", completionEventType: "Sensor Stop");

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            StartEventType = blank,
            CompletionEventType = blank,
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        saved()!.StartEventType.Should().BeNull();
        saved()!.CompletionEventType.Should().BeNull();
    }

    [Fact]
    public async Task UpdateDefinition_ReplacesAnEventTypeWithTheCanonicalName()
    {
        var (existing, saved) = ArrangeStored([], null, startEventType: "Sensor Start");

        await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            StartEventType = "pod change",
        });

        saved()!.StartEventType.Should().Be("Pod Change");
    }

    [Fact]
    public void GetDeviceEventTypes_ListsOnlyNamesTheServerAccepts()
    {
        var names = CreateController().GetDeviceEventTypes().Result
            .Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<string[]>().Subject;

        names.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        names.Where(name =>
                !(TrackerEventTypes.TryNormaliseDeviceEvent(name, "StartEventType", out var canonical, out _) && canonical == name))
            .Should().BeEmpty();
    }

    private void FailEventWrites() =>
        _deviceEvents
            .Setup(d => d.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("device event store unavailable"));

    [Fact]
    public async Task StartInstance_WhenTheEventWriteFails_StillStarts()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration, startEventType: "Sensor Start");
        FailEventWrites();

        var result = await CreateController().StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        _repository.Verify(r => r.StartInstanceAsync(
            definition.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteInstance_WhenTheEventWriteFails_StillCompletesAndBroadcasts()
    {
        var run = Running(Now.AddDays(-9));
        var definition = ArrangeDefinition(TrackerMode.Duration, [run], completionEventType: "Sensor Stop");
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                run.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Instance(definition, run) with
            {
                CompletedAt = Now,
                CompletionReason = CompletionReason.Completed,
            });
        FailEventWrites();

        var result = await CreateController().CompleteInstance(
            run.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        result.Result.Should().BeOfType<OkObjectResult>();
        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync(
            TrackerRealtimeAction.Complete, It.Is<TrackerInstanceDto>(d => d.Id == run.Id)), Times.Once);
    }

    [Fact]
    public async Task UpdateDefinition_RepostingAStoredEventTypeThatIsNoDeviceEvent_Saves()
    {
        var (existing, saved) = ArrangeStored([], null, startEventType: "Note");

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            Name = "Renamed",
            StartEventType = "note",
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        saved()!.Name.Should().Be("Renamed");
        saved()!.StartEventType.Should().Be("Note");
    }

    [Fact]
    public async Task UpdateDefinition_ChangingAStoredEventTypeToAnotherThatIsNoDeviceEvent_Is400()
    {
        var (existing, saved) = ArrangeStored([], null, startEventType: "Note");

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            StartEventType = "Meal Bolus",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        saved().Should().BeNull();
    }
}
