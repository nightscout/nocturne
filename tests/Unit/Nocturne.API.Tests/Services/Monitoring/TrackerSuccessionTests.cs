using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerSuccessionTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 10, 43, 30, DateTimeKind.Utc);

    private readonly Mock<ITrackerRepository> _repository = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly List<(TrackerRealtimeAction Action, Guid InstanceId)> _broadcasts = [];

    public TrackerSuccessionTests()
    {
        _repository
            .Setup(r => r.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(), It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _, CancellationToken ct) => work(_repository.Object, ct));

        _broadcast
            .Setup(b => b.BroadcastTrackerUpdateAsync(
                It.IsAny<TrackerRealtimeAction>(), It.IsAny<TrackerInstanceDto>(),
                It.IsAny<string>(), It.IsAny<TrackerVisibility>()))
            .Callback((TrackerRealtimeAction action, TrackerInstanceDto instance, string _, TrackerVisibility _) =>
                _broadcasts.Add((action, instance.Id)))
            .Returns(Task.CompletedTask);
    }

    private static TrackerDefinitionEntity Definition(TrackerMode mode = TrackerMode.Duration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Infusion Site",
        UserId = "owner",
        Mode = mode,
        LifespanHours = 72,
    };

    private TrackerInstanceEntity GivenRunning(TrackerDefinitionEntity definition, DateTime startedAt)
    {
        var instance = new TrackerInstanceEntity
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            UserId = definition.UserId,
            StartedAt = startedAt,
            Definition = definition,
        };
        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([instance]);
        _repository
            .Setup(r => r.CompleteInstanceAsync(
                instance.Id, It.IsAny<CompletionReason>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instance);
        return instance;
    }

    private static TrackerInstanceEntity NewRun(TrackerDefinitionEntity definition) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = definition.Id,
        UserId = definition.UserId,
        StartedAt = T0,
        Definition = definition,
    };

    private Task<TrackerSuccessionResult> StartAsync(TrackerDefinitionEntity definition, TrackerInstanceEntity started) =>
        TrackerSuccession.StartAsync(
            _repository.Object,
            _broadcast.Object,
            NullLogger.Instance,
            definition,
            T0,
            completionNotes: null,
            completeTreatmentId: null,
            (_, _) => Task.FromResult(started),
            CancellationToken.None);

    [Fact]
    public async Task ReplacingTheRunningInstance_BroadcastsCompleteThenCreate()
    {
        var definition = Definition();
        var running = GivenRunning(definition, T0.AddDays(-3));
        var started = NewRun(definition);

        var result = await StartAsync(definition, started);

        result.Outcome.Should().Be(TrackerSuccessionOutcome.Started);
        _broadcasts.Should().Equal(
            (TrackerRealtimeAction.Complete, running.Id),
            (TrackerRealtimeAction.Create, started.Id));
    }

    [Fact]
    public async Task StartingWithNothingRunning_BroadcastsOnlyCreate()
    {
        var definition = Definition();
        _repository
            .Setup(r => r.GetActiveInstancesForDefinitionAsync(definition.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var started = NewRun(definition);

        await StartAsync(definition, started);

        _broadcasts.Should().Equal((TrackerRealtimeAction.Create, started.Id));
    }

    [Fact]
    public async Task ARunningInstanceStartedLater_BroadcastsNothing()
    {
        var definition = Definition();
        GivenRunning(definition, T0.AddHours(1));

        var result = await StartAsync(definition, NewRun(definition));

        result.Outcome.Should().Be(TrackerSuccessionOutcome.NewerRunning);
        _broadcasts.Should().BeEmpty();
    }
}
