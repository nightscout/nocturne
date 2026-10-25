using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class LegacyFallbackServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private const string UserId = "11111111-1111-1111-1111-111111111111";

    private readonly List<TrackerDefinitionEntity> _definitions = [];
    private readonly List<TrackerInstanceEntity> _instances = [];
    private readonly Dictionary<DeviceEventType, List<DeviceEvent>> _events = [];
    private readonly List<PatientDevice> _devices = [];
    private readonly Mock<ITrackerRepository> _trackers = new();
    private readonly Mock<IDeviceEventRepository> _deviceEvents = new();
    private readonly Mock<ITrackerAlertRuleSyncService> _ruleSync = new();
    private readonly Mock<IDeviceEventReactor> _reactor = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly LegacyFallbackService _service;

    /// <summary>Runs inside the consumable lock before the work; a throw fails that kind.</summary>
    private Func<Task>? _beforeWork;

    /// <summary>Runs inside the consumable lock after the work, before its commit.</summary>
    private Action? _beforeCommit;

    public LegacyFallbackServiceTests()
    {
        _trackers.Setup(t => t.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _definitions]);
        _trackers.Setup(t => t.GetDefinitionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _definitions.FirstOrDefault(d => d.Id == id));
        _trackers.Setup(t => t.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackerDefinitionEntity d, CancellationToken _) =>
            {
                d.Id = Guid.NewGuid();
                _definitions.Add(d);
                return d;
            });
        _trackers.Setup(t => t.GetActiveInstancesForDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
                _instances.Where(i => i.DefinitionId == id && i.CompletedAt == null).ToList());
        _trackers.Setup(t => t.StartInstanceAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid definitionId, string createdBy, string? _, string? startTreatmentId,
                DateTime? startedAt, DateTime? _, CancellationToken _) =>
            {
                var instance = new TrackerInstanceEntity
                {
                    Id = Guid.NewGuid(),
                    DefinitionId = definitionId,
                    CreatedBy = createdBy,
                    StartTreatmentId = startTreatmentId,
                    StartedAt = startedAt!.Value,
                    Definition = _definitions.Single(d => d.Id == definitionId),
                };
                _instances.Add(instance);
                return instance;
            });
        _trackers.Setup(t => t.GetInstanceByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _instances.FirstOrDefault(i => i.Id == id));
        _trackers.Setup(t => t.ExecuteUnderConsumableLockAsync(
                It.IsAny<Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<It.IsAnyType>>>(),
                It.IsAny<Func<ITrackerRepository, It.IsAnyType, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation => Invoke(
                nameof(RunUnderConsumableLockAsync), invocation, invocation.Arguments[0], invocation.Arguments[2])));
        _trackers.Setup(t => t.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Func<ITrackerRepository, CancellationToken, Task<It.IsAnyType>>>(),
                It.IsAny<Func<ITrackerRepository, It.IsAnyType, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation => Invoke(
                nameof(RunUnderDefinitionLockAsync), invocation, invocation.Arguments[1], invocation.Arguments[3])));

        _deviceEvents.Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEventType[] types, Guid? _, CancellationToken _) => types
                .SelectMany(t => _events.GetValueOrDefault(t) ?? [])
                .MaxBy(e => e.Timestamp));
        _deviceEvents.Setup(r => r.CountByEventTypesSinceAsync(
                It.IsAny<DeviceEventType[]>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEventType[] types, DateTime since, CancellationToken _) => types
                .SelectMany(t => _events.GetValueOrDefault(t) ?? [])
                .Count(e => e.Timestamp >= since));

        var patientDevices = new Mock<IPatientDeviceRepository>();
        patientDevices.Setup(r => r.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _devices]);

        var time = new FakeTimeProvider(Now);
        var writer = new ConsumableTrackerWriter(
            _deviceEvents.Object, _ruleSync.Object, _reactor.Object, _broadcast.Object, time,
            NullLogger<ConsumableTrackerWriter>.Instance);
        _service = new LegacyFallbackService(
            _trackers.Object, _deviceEvents.Object, patientDevices.Object, writer, time,
            NullLogger<LegacyFallbackService>.Instance);
    }

    private object Invoke(string helper, IInvocation invocation, object work, object ct) =>
        typeof(LegacyFallbackServiceTests)
            .GetMethod(helper, BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(invocation.Method.GetGenericArguments()[0])
            .Invoke(this, [work, ct])!;

    private async Task<T> RunUnderConsumableLockAsync<T>(
        Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (_beforeWork is not null)
            await _beforeWork();
        var result = await work(_trackers.Object, null!, ct);
        _beforeCommit?.Invoke();
        return result;
    }

    private Task<T> RunUnderDefinitionLockAsync<T>(
        Func<ITrackerRepository, CancellationToken, Task<T>> work, CancellationToken ct) =>
        work(_trackers.Object, ct);

    private DeviceEvent Event(DeviceEventType type, DateTime at)
    {
        var deviceEvent = new DeviceEvent { Id = Guid.NewGuid(), EventType = type, Timestamp = at };
        if (!_events.TryGetValue(type, out var list))
            _events[type] = list = [];
        list.Add(deviceEvent);
        return deviceEvent;
    }

    private void ExistingTracker(params string[] triggers) =>
        _definitions.Add(new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            Mode = TrackerMode.Duration,
            TriggerEventTypes = JsonSerializer.Serialize(triggers),
        });

    [Fact]
    public async Task AnUncoveredKindWithARecentEvent_HasAFallback()
    {
        var change = Event(DeviceEventType.SiteChange, Now.AddHours(-30));

        var fallbacks = (await _service.GetAsync(readDeviceAges: true)).Fallbacks;

        var site = fallbacks.Should().ContainSingle().Subject;
        site.Kind.Should().Be(LegacyConsumableKind.Site);
        site.LatestEvent.Id.Should().Be(change.Id);
        site.Spec.Should().BeEquivalentTo(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site));
    }

    [Fact]
    public async Task ACoveredKind_HasNoFallback()
    {
        ExistingTracker("Site Change");
        Event(DeviceEventType.CannulaChange, Now.AddHours(-30));

        (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().BeEmpty();
    }

    [Fact]
    public async Task ACoveredKind_NamesEveryDefinitionCoveringIt()
    {
        ExistingTracker("Site Change");
        ExistingTracker("Pod Change");
        ExistingTracker("Sensor Start");
        Event(DeviceEventType.CannulaChange, Now.AddHours(-30));

        var covered = (await _service.GetAsync(readDeviceAges: true)).Covered;

        covered.Select(c => c.Kind).Should().Equal(LegacyConsumableKind.Sensor, LegacyConsumableKind.Site);
        covered.Single(c => c.Kind == LegacyConsumableKind.Site).DefinitionIds
            .Should().Equal(_definitions[0].Id, _definitions[1].Id);
        covered.Single(c => c.Kind == LegacyConsumableKind.Sensor).DefinitionIds
            .Should().Equal(_definitions[2].Id);
    }

    [Fact]
    public async Task WithoutDeviceAges_OnlyCoverageIsRead()
    {
        ExistingTracker("Sensor Start");
        Event(DeviceEventType.SiteChange, Now.AddHours(-30));

        var read = await _service.GetAsync(readDeviceAges: false);

        read.Fallbacks.Should().BeEmpty();
        read.Covered.Should().ContainSingle().Which.Kind.Should().Be(LegacyConsumableKind.Sensor);
        _deviceEvents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnUncoveredKind_IsNotListedAsCovered()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-30));

        var read = await _service.GetAsync(readDeviceAges: true);

        read.Covered.Should().BeEmpty();
        read.Fallbacks.Should().ContainSingle();
    }

    [Fact]
    public async Task ThePumpBattery_NeedsTwoChangesInTheWindow()
    {
        Event(DeviceEventType.PumpBatteryChange, Now.AddDays(-5));
        (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().BeEmpty();

        Event(DeviceEventType.PumpBatteryChange, Now.AddDays(-26));
        (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().ContainSingle()
            .Which.Kind.Should().Be(LegacyConsumableKind.PumpBattery);
    }

    [Fact]
    public async Task ARegisteredDevice_SetsTheSpecAndItsEvents()
    {
        _devices.Add(new PatientDevice { CatalogId = "omnipod-dash" });
        Event(DeviceEventType.PodChange, Now.AddHours(-10));

        var fallback = (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().ContainSingle().Subject;

        fallback.Spec.Name.Should().Be("Omnipod DASH pod");
        fallback.Projected.Definition.LifespanHours.Should().Be(fallback.Spec.LifespanHours);
    }

    [Fact]
    public async Task APatchPumpTenant_IsNotOfferedAReservoir()
    {
        _devices.Add(new PatientDevice { CatalogId = "omnipod-dash" });
        Event(DeviceEventType.ReservoirChange, Now.AddHours(-10));

        (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().BeEmpty();
        (await _service.CreateTrackersAsync([LegacyConsumableKind.Reservoir], UserId)).Definitions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFallbacksSchedule_IsTheScheduleOfTheTrackerItConvertsInto()
    {
        Event(DeviceEventType.SensorStart, Now.AddDays(-9));
        var fallback = LegacyFallbackDto.From((await _service.GetAsync(readDeviceAges: true)).Fallbacks.Single());

        var conversion = await _service.CreateTrackersAsync([LegacyConsumableKind.Sensor], UserId);

        var run = TrackerInstanceDto.FromEntity(conversion.Instances.Single());
        run.Schedule.Should().BeEquivalentTo(fallback.Schedule, o => o.WithStrictOrdering());
        run.StartedAt.Should().Be(fallback.LatestEventAt);
        run.ExpectedEndAt.Should().Be(fallback.ExpectedEndAt);
        fallback.Schedule.Select(s => s.Urgency)
            .Should().Equal(NotificationUrgency.Warn, NotificationUrgency.Hazard);
    }

    [Fact]
    public async Task Converting_CommitsTheDefinitionItsRulesAndItsRunTogether_AtTheLatestEvent()
    {
        Event(DeviceEventType.SensorStart, Now.AddDays(-12));
        var latest = Event(DeviceEventType.SensorChange, Now.AddDays(-2));
        var rulesCreatedInTheLock = false;
        _ruleSync.Setup(s => s.CreateRulesAsync(It.IsAny<DbUnitOfWork>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => rulesCreatedInTheLock = _instances.Count == 0)
            .Returns(Task.CompletedTask);

        var conversion = await _service.CreateTrackersAsync([LegacyConsumableKind.Sensor], UserId);

        var definition = conversion.Definitions.Should().ContainSingle().Subject;
        definition.CreatedBy.Should().Be(UserId);
        definition.NotificationThresholds.Should().HaveCount(2);
        rulesCreatedInTheLock.Should().BeTrue();
        _ruleSync.Verify(s => s.CreateRulesAsync(It.IsAny<DbUnitOfWork>(), definition.Id, It.IsAny<CancellationToken>()), Times.Once);
        _ruleSync.Verify(s => s.SyncDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _trackers.Verify(t => t.ExecuteUnderDefinitionLockAsync(
            definition.Id,
            It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
            It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(definition.Id), Times.Once);

        var instance = conversion.Instances.Should().ContainSingle().Subject;
        instance.StartTreatmentId.Should().Be(latest.Id.ToString());
        instance.StartedAt.Should().Be(latest.Timestamp);
        _broadcast.Verify(b => b.BroadcastTrackerUpdateAsync(
            TrackerRealtimeAction.Create, It.Is<TrackerInstanceDto>(d => d.Id == instance.Id)), Times.Once);
        _reactor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnEventArrivingBeforeTheCommit_IsOfferedToTheNewTracker()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-40));
        DeviceEvent? arrived = null;
        _beforeCommit = () => arrived ??= Event(DeviceEventType.CannulaChange, Now.AddMinutes(-5));

        await _service.CreateTrackersAsync([LegacyConsumableKind.Site], UserId);

        _reactor.Verify(r => r.OnCreatedAsync(
            It.Is<IReadOnlyList<DeviceEvent>>(e => e.Single().Id == arrived!.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConvertingTwice_CreatesOneTrackerAndReportsNothingTheSecondTime()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));

        await _service.CreateTrackersAsync([LegacyConsumableKind.Site], UserId);
        var second = await _service.CreateTrackersAsync([LegacyConsumableKind.Site, LegacyConsumableKind.Site], UserId);

        second.Definitions.Should().BeEmpty();
        second.Instances.Should().BeEmpty();
        _definitions.Should().ContainSingle();
        _instances.Should().ContainSingle();
    }

    [Fact]
    public async Task Converting_SkipsKindsCoveredByAnotherTrackerAndKindsWithoutAFallback()
    {
        ExistingTracker("Insulin Change");
        Event(DeviceEventType.ReservoirChange, Now.AddHours(-20));
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));

        var conversion = await _service.CreateTrackersAsync(
            [LegacyConsumableKind.Reservoir, LegacyConsumableKind.Site, LegacyConsumableKind.Sensor], UserId);

        conversion.Definitions.Should().ContainSingle().Which.Category.Should().Be(TrackerCategory.Cannula);
        _definitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task OneKindsTransactionFailing_LeavesTheOthersConvertedAndIsReported()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));
        Event(DeviceEventType.SensorStart, Now.AddDays(-2));
        var calls = 0;
        _beforeWork = () => ++calls == 1 ? throw new DbUpdateException("lost") : Task.CompletedTask;

        var conversion = await _service.CreateTrackersAsync(
            [LegacyConsumableKind.Sensor, LegacyConsumableKind.Site], UserId);

        conversion.FailedKinds.Should().Equal(LegacyConsumableKind.Sensor);
        conversion.Definitions.Should().ContainSingle().Which.Category.Should().Be(TrackerCategory.Cannula);
    }

    [Fact]
    public async Task AKindWhoseRetriesRunOut_IsReportedAndTheOthersConvert()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));
        Event(DeviceEventType.SensorStart, Now.AddDays(-2));
        var calls = 0;
        _beforeWork = () => ++calls == 1
            ? throw new RetryLimitExceededException("retries exhausted", new TimeoutException())
            : Task.CompletedTask;

        var conversion = await _service.CreateTrackersAsync(
            [LegacyConsumableKind.Sensor, LegacyConsumableKind.Site], UserId);

        conversion.FailedKinds.Should().Equal(LegacyConsumableKind.Sensor);
        conversion.Definitions.Should().ContainSingle().Which.Category.Should().Be(TrackerCategory.Cannula);
    }


    [Fact]
    public async Task AFailureAfterTheCommit_StillReportsTheKindAsConverted()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));
        _broadcast.Setup(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new DbUpdateException("lost"));

        var conversion = await _service.CreateTrackersAsync([LegacyConsumableKind.Site], UserId);

        conversion.FailedKinds.Should().BeEmpty();
        conversion.Definitions.Should().ContainSingle().Which.Category.Should().Be(TrackerCategory.Cannula);
        conversion.Instances.Should().ContainSingle();
    }

    [Fact]
    public async Task ACommitThatReportedFailure_LandedWhenItsDefinitionAndRunExist()
    {
        Event(DeviceEventType.SiteChange, Now.AddHours(-20));
        Delegate? verify = null;
        _trackers.Setup(t => t.ExecuteUnderConsumableLockAsync(
                It.IsAny<Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<It.IsAnyType>>>(),
                It.IsAny<Func<ITrackerRepository, It.IsAnyType, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new InvocationFunc(invocation =>
            {
                verify = (Delegate)invocation.Arguments[1];
                return Invoke(nameof(RunUnderConsumableLockAsync), invocation, invocation.Arguments[0], invocation.Arguments[2]);
            }));
        await _service.CreateTrackersAsync([LegacyConsumableKind.Site], UserId);
        var check = (Func<ITrackerRepository, CreatedConsumableTracker?, CancellationToken, Task<bool>>)verify!;

        var definition = _definitions.Single();
        var run = _instances.Single();
        var spec = ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site);
        var empty = new Mock<ITrackerRepository>().Object;

        (await check(empty, null, default)).Should().BeTrue();
        (await check(_trackers.Object, new(definition, spec, run, null), default)).Should().BeTrue();
        (await check(_trackers.Object, new(definition, spec, null, null), default)).Should().BeTrue();
        (await check(_trackers.Object, new(definition, spec, new TrackerInstanceEntity { Id = Guid.NewGuid() }, null), default))
            .Should().BeFalse();
        (await check(empty, new(definition, spec, null, null), default)).Should().BeFalse();
    }

    [Fact]
    public async Task AHandRunSensorTracker_HidesTheSensorFallback()
    {
        _definitions.Add(new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(), Name = "My sensor", Category = TrackerCategory.Sensor, Mode = TrackerMode.Duration,
        });
        Event(DeviceEventType.SensorStart, Now.AddDays(-2));

        (await _service.GetAsync(readDeviceAges: true)).Fallbacks.Should().BeEmpty();
        (await _service.CreateTrackersAsync([LegacyConsumableKind.Sensor], UserId)).Definitions.Should().BeEmpty();
    }
}
