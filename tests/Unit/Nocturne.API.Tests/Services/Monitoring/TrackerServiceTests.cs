using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

/// <summary>
/// A definition write is stored, then its managed rules are synced, then it is broadcast; create
/// and update run those steps alike, and a write that is refused or fails to store runs none.
/// </summary>
[Trait("Category", "Unit")]
public class TrackerServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITrackerRepository> _repository = new();
    private readonly Mock<ITrackerAlertRuleSyncService> _ruleSync = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly List<string> _steps = [];
    private readonly TrackerService _service;

    public TrackerServiceTests()
    {
        _ruleSync.Setup(s => s.SyncDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, CancellationToken _) => _steps.Add($"sync {id}"))
            .Returns(Task.CompletedTask);
        _broadcast.Setup(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()))
            .Callback((Guid id) => _steps.Add($"broadcast {id}"))
            .Returns(Task.CompletedTask);

        _service = new TrackerService(
            _repository.Object,
            _broadcast.Object,
            _ruleSync.Object,
            Mock.Of<IAlertRepository>(),
            Mock.Of<ITenantAccessor>(),
            Mock.Of<IAlertAcknowledgementService>(),
            new TrackerDeviceEventWriter(
                Mock.Of<IDeviceEventRepository>(),
                Mock.Of<IPatientDeviceStamper>(),
                NullLogger<TrackerDeviceEventWriter>.Instance),
            new FakeTimeProvider(Now),
            NullLogger<TrackerService>.Instance);
    }

    private TrackerDefinition GivenStored()
    {
        var stored = TrackerDefinition.Restore(Guid.NewGuid(), "Sensor", createdBy: "owner", lifespanHours: 240);
        _repository.Setup(r => r.GetDefinitionByIdAsync(stored.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        return stored;
    }

    private static readonly TrackerNotificationThreshold Warning = new(Guid.Empty, NotificationUrgency.Warn, -24, null, 0);

    [Fact]
    public async Task Create_StoresThenSyncsThenBroadcasts()
    {
        var id = Guid.NewGuid();
        _repository.Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("store"))
            .ReturnsAsync((TrackerDefinition d, CancellationToken _) => d.Stored(id));

        var result = await _service.CreateDefinitionAsync(new TrackerDefinitionDraft { Name = "Sensor" }, "owner", default);

        result.Definition!.Id.Should().Be(id);
        _steps.Should().Equal("store", $"sync {id}", $"broadcast {id}");
    }

    [Fact]
    public async Task Update_StoresThenSyncsThenBroadcasts_AsCreateDoes()
    {
        var stored = GivenStored();
        _repository.Setup(r => r.UpdateNotificationThresholdsAsync(
                stored.Id, It.IsAny<IReadOnlyList<TrackerNotificationThreshold>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("store thresholds"))
            .Returns(Task.CompletedTask);
        _repository.Setup(r => r.UpdateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .Callback(() => _steps.Add("store"))
            .ReturnsAsync((TrackerDefinition d, CancellationToken _) => d);

        var result = await _service.UpdateDefinitionAsync(
            stored.Id, new TrackerDefinitionEdit { Name = "Renamed", NotificationThresholds = [Warning] }, default);

        result!.Definition!.Name.Should().Be("Renamed");
        _steps.Should().Equal("store thresholds", "store", $"sync {stored.Id}", $"broadcast {stored.Id}");
    }

    [Fact]
    public async Task Update_WithNoThresholds_LeavesTheStoredOnes()
    {
        var stored = GivenStored();
        _repository.Setup(r => r.UpdateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerDefinition d, CancellationToken _) => d);

        await _service.UpdateDefinitionAsync(stored.Id, new TrackerDefinitionEdit { Name = "Renamed" }, default);

        _repository.Verify(r => r.UpdateNotificationThresholdsAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<TrackerNotificationThreshold>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACreateThatFailsToStore_RunsNoStepAfterIt()
    {
        _repository.Setup(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var act = () => _service.CreateDefinitionAsync(new TrackerDefinitionDraft { Name = "Sensor" }, "owner", default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdateThatFailsToStore_RunsNoStepAfterIt()
    {
        var stored = GivenStored();
        _repository.Setup(r => r.UpdateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var act = () => _service.UpdateDefinitionAsync(stored.Id, new TrackerDefinitionEdit { Name = "Renamed" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUpdateWhoseThresholdsFailToStore_StoresNothingElseAndRunsNoStep()
    {
        var stored = GivenStored();
        _repository.Setup(r => r.UpdateNotificationThresholdsAsync(
                stored.Id, It.IsAny<IReadOnlyList<TrackerNotificationThreshold>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var act = () => _service.UpdateDefinitionAsync(
            stored.Id, new TrackerDefinitionEdit { NotificationThresholds = [Warning] }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _repository.Verify(r => r.UpdateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedCreate_StoresNothingAndRunsNoStep()
    {
        var result = await _service.CreateDefinitionAsync(
            new TrackerDefinitionDraft { Name = "Visit", Mode = TrackerMode.Event, LifespanHours = 24 }, "owner", default);

        result.Error!.Rule.Should().Be(TrackerDefinitionRule.Lifespan);
        _repository.Verify(r => r.CreateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedUpdate_StoresNothingAndRunsNoStep()
    {
        var stored = GivenStored();

        var result = await _service.UpdateDefinitionAsync(
            stored.Id, new TrackerDefinitionEdit { TriggerEventTypes = ["Pump Suspend"] }, default);

        result!.Error!.Rule.Should().Be(TrackerDefinitionRule.Triggers);
        _repository.Verify(r => r.UpdateDefinitionAsync(It.IsAny<TrackerDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
        _steps.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatingADefinitionThatIsNotStored_IsNotFound() =>
        (await _service.UpdateDefinitionAsync(Guid.NewGuid(), new TrackerDefinitionEdit { Name = "Renamed" }, default))
            .Should().BeNull();
}
