using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
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

    public TrackersControllerStartTests()
    {
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));
    }

    private TrackersController CreateController(string subjectId = OwnerId, bool isAdmin = false)
    {
        var controller = new TrackersController(
            _repository.Object,
            _broadcast.Object,
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<INocturneDbContextFactory>(),
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<ILogger<TrackersController>>()
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.Parse(subjectId),
        };
        if (isAdmin)
        {
            var trie = new PermissionTrie();
            trie.Add("*");
            httpContext.Items["PermissionTrie"] = trie;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private TrackerDefinitionEntity ArrangeDefinition(TrackerMode mode, params TrackerInstanceEntity[] running) =>
        ArrangeDefinition(mode, TrackerVisibility.Private, running);

    private TrackerDefinitionEntity ArrangeDefinition(
        TrackerMode mode, TrackerVisibility visibility, params TrackerInstanceEntity[] running)
    {
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            UserId = OwnerId,
            Name = "Sensor",
            Mode = mode,
            Visibility = visibility,
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
                    UserId = userId,
                    StartedAt = startedAt ?? Now,
                    ScheduledAt = scheduledAt,
                });
        return definition;
    }

    private static TrackerInstanceEntity Running(DateTime startedAt) =>
        new() { Id = Guid.NewGuid(), UserId = OwnerId, StartedAt = startedAt };

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
    public async Task StartInstance_ByAnAdminOnAMembersPrivateTracker_CompleteAndCreateReachOnlyTheOwner()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, old);
        var sent = new List<(TrackerRealtimeAction Action, string Owner, TrackerVisibility Visibility)>();
        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(
                It.IsAny<TrackerRealtimeAction>(), It.IsAny<TrackerInstanceDto>(), It.IsAny<string>(), It.IsAny<TrackerVisibility>()))
            .Callback((TrackerRealtimeAction action, TrackerInstanceDto _, string owner, TrackerVisibility visibility) =>
                sent.Add((action, owner, visibility)))
            .Returns(Task.CompletedTask);

        var result = await CreateController(AdminId, isAdmin: true).StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        sent.Should().Equal(
            (TrackerRealtimeAction.Complete, OwnerId, TrackerVisibility.Private),
            (TrackerRealtimeAction.Create, OwnerId, TrackerVisibility.Private));
    }

    [Fact]
    public async Task StartInstance_ByAnAdminOnAMembersPublicTracker_CompleteAndCreateShareOneAudience()
    {
        var old = Running(Now.AddDays(-3));
        var definition = ArrangeDefinition(TrackerMode.Duration, TrackerVisibility.Public, old);
        var sent = new List<(TrackerRealtimeAction Action, string Owner, TrackerVisibility Visibility)>();
        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(
                It.IsAny<TrackerRealtimeAction>(), It.IsAny<TrackerInstanceDto>(), It.IsAny<string>(), It.IsAny<TrackerVisibility>()))
            .Callback((TrackerRealtimeAction action, TrackerInstanceDto _, string owner, TrackerVisibility visibility) =>
                sent.Add((action, owner, visibility)))
            .Returns(Task.CompletedTask);

        await CreateController(AdminId, isAdmin: true).StartInstance(
            new StartTrackerInstanceRequest { DefinitionId = definition.Id, StartedAt = Now });

        sent.Should().Equal(
            (TrackerRealtimeAction.Complete, OwnerId, TrackerVisibility.Public),
            (TrackerRealtimeAction.Create, OwnerId, TrackerVisibility.Public));
    }

    private List<(TrackerRealtimeAction Action, string Owner, TrackerVisibility Visibility)> RecordBroadcasts()
    {
        var sent = new List<(TrackerRealtimeAction Action, string Owner, TrackerVisibility Visibility)>();
        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(
                It.IsAny<TrackerRealtimeAction>(), It.IsAny<TrackerInstanceDto>(), It.IsAny<string>(), It.IsAny<TrackerVisibility>()))
            .Callback((TrackerRealtimeAction action, TrackerInstanceDto _, string owner, TrackerVisibility visibility) =>
                sent.Add((action, owner, visibility)))
            .Returns(Task.CompletedTask);
        return sent;
    }

    private TrackerInstanceEntity ArrangeAdminStartedRun()
    {
        var run = new TrackerInstanceEntity { Id = Guid.NewGuid(), UserId = AdminId, StartedAt = Now.AddDays(-1) };
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
    public async Task CompleteInstance_OnAnAdminStartedRun_ReachesTheDefinitionsOwner()
    {
        var run = ArrangeAdminStartedRun();
        var sent = RecordBroadcasts();

        await CreateController(AdminId, isAdmin: true).CompleteInstance(
            run.Id, new CompleteTrackerInstanceRequest { Reason = CompletionReason.Completed });

        sent.Should().Equal((TrackerRealtimeAction.Complete, OwnerId, TrackerVisibility.Private));
    }

    [Fact]
    public async Task DeleteInstance_OnAnAdminStartedRun_ReachesTheDefinitionsOwner()
    {
        var run = ArrangeAdminStartedRun();
        var sent = RecordBroadcasts();

        await CreateController(AdminId, isAdmin: true).DeleteInstance(run.Id);

        sent.Should().Equal((TrackerRealtimeAction.Delete, OwnerId, TrackerVisibility.Private));
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
    public async Task ApplyPreset_BelongingToSomeoneElse_IsForbidden()
    {
        var definition = ArrangeDefinition(TrackerMode.Duration);
        var preset = new TrackerPresetEntity
        {
            Id = Guid.NewGuid(),
            UserId = "22222222-2222-2222-2222-222222222222",
            DefinitionId = definition.Id,
            Definition = definition,
        };
        _repository
            .Setup(r => r.GetPresetByIdAsync(preset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(preset);

        var result = await CreateController().ApplyPreset(preset.Id);

        result.Result.Should().BeOfType<ForbidResult>();
        _repository.Verify(r => r.StartInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
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
            UserId = OwnerId,
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
    public async Task UpdateDefinition_DropsAStoredTriggerThatPredatesTheList_InsteadOfRejectingTheSave()
    {
        var existing = ArrangeStored("[\"Pump Suspend\",\"Sensor Start\"]", null);

        var result = await CreateController().UpdateDefinition(existing.Id, new UpdateTrackerDefinitionRequest
        {
            TriggerEventTypes = ["Pump Suspend", "Sensor Start"],
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        existing.TriggerEventTypes.Should().Be("[\"Sensor Start\"]");
    }
}
