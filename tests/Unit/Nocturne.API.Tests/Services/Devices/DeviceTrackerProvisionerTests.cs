using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceTrackerProvisionerTests
{
    private const string UserId = "user-1";

    private readonly Mock<ITrackerRepository> _trackers = new();
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<ITrackerAlertRuleSyncService> _ruleSync = new();
    private readonly Mock<IDeviceEventRepository> _deviceEvents = new();
    private readonly List<TrackerDefinitionEntity> _created = [];
    private readonly List<TrackerInstanceEntity> _started = [];
    private readonly List<TrackerDefinitionEntity> _existing = [];
    private readonly DeviceTrackerProvisioner _provisioner;

    public DeviceTrackerProvisionerTests()
    {
        _trackers.Setup(t => t.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [.. _existing]);
        _trackers.Setup(t => t.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinitionEntity, CancellationToken>((d, _) => _created.Add(d))
            .ReturnsAsync((TrackerDefinitionEntity d, CancellationToken _) =>
            {
                d.Id = Guid.NewGuid();
                return d;
            });
        _trackers.Setup(t => t.ExecuteUnderConsumableLockAsync(
                It.IsAny<Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<List<CreatedConsumableTracker>>>>(),
                It.IsAny<Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns((Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<List<CreatedConsumableTracker>>> work,
                Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>? _,
                CancellationToken ct) => work(_trackers.Object, null!, ct));
        _trackers.Setup(t => t.ExecuteUnderDefinitionLockAsync(
                It.IsAny<Guid>(),
                It.IsAny<Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>>>(),
                It.IsAny<Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns((Guid _, Func<ITrackerRepository, CancellationToken, Task<TrackerSuccessionResult>> work,
                Func<ITrackerRepository, TrackerSuccessionResult, CancellationToken, Task<bool>>? _,
                CancellationToken ct) => work(_trackers.Object, ct));
        _trackers.Setup(t => t.GetActiveInstancesForDefinitionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _started.Where(i => i.DefinitionId == id).ToList());
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
                    Definition = _created.Single(d => d.Id == definitionId),
                };
                _started.Add(instance);
                return instance;
            });

        var writer = new ConsumableTrackerWriter(
            _deviceEvents.Object, _ruleSync.Object, Mock.Of<IDeviceEventReactor>(), _broadcast.Object,
            TimeProvider.System, NullLogger<ConsumableTrackerWriter>.Instance);
        _provisioner = new DeviceTrackerProvisioner(
            _trackers.Object, writer, NullLogger<DeviceTrackerProvisioner>.Instance);
    }

    private static PatientDevice Device(string catalogId, DeviceCategory category) =>
        new() { CatalogId = catalogId, DeviceCategory = category };

    [Fact]
    public async Task APatchPumpGetsOnePodTrackerRestartedByAPodOrSiteChange()
    {
        var created = await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        created.Should().Be(1);
        var pod = _created.Single();
        pod.Name.Should().Be("Omnipod DASH pod");
        pod.Category.Should().Be(TrackerCategory.Pod);
        pod.LifespanHours.Should().Be(72);
        pod.TriggerEventTypes.Should().Contain("Pod Change").And.Contain("Site Change");
    }

    [Fact]
    public async Task ATubedPumpGetsSiteAndReservoirTrackers()
    {
        await _provisioner.ProvisionAsync(Device("dana-i", DeviceCategory.InsulinPump), UserId);

        _created.Select(d => d.Category).Should().BeEquivalentTo(
            [TrackerCategory.Cannula, TrackerCategory.Reservoir]);
    }

    [Fact]
    public async Task ACgmSensorTrackerLastsTheCatalogsSensorDuration()
    {
        await _provisioner.ProvisionAsync(Device("libre-3", DeviceCategory.CGM), UserId);

        var sensor = _created.Single();
        sensor.Category.Should().Be(TrackerCategory.Sensor);
        sensor.LifespanHours.Should().Be(14 * 24);
        sensor.StartEventType.Should().Be("Sensor Start");
    }

    [Fact]
    public async Task ASeparateTransmitterGetsItsOwnTracker()
    {
        await _provisioner.ProvisionAsync(Device("dexcom-g6", DeviceCategory.CGM), UserId);

        _created.Select(d => d.Name).Should().BeEquivalentTo(["Dexcom G6 sensor", "Dexcom G6 transmitter"]);
        _created.Single(d => d.Name.EndsWith("transmitter")).LifespanHours.Should().Be(90 * 24);
    }

    [Fact]
    public async Task ATrackerTheTenantAlreadyHasIsNotCreatedAgain()
    {
        _trackers.Setup(t => t.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackerDefinitionEntity { CreatedBy = UserId, Name = "omnipod dash POD" }]);

        var created = await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        created.Should().Be(0);
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task ATrackerAnotherMemberCreatedIsNotDuplicatedForASecondMember()
    {
        _trackers.Setup(t => t.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackerDefinitionEntity { CreatedBy = "another-member", Name = "Omnipod DASH pod" }]);

        var created = await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        created.Should().Be(0);
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task EachCreatedTrackerIsAnnouncedToTheTenant()
    {
        await _provisioner.ProvisionAsync(Device("dana-i", DeviceCategory.InsulinPump), UserId);

        foreach (var definition in _created)
            _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(definition.Id), Times.Once);
        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NothingIsAnnouncedWhenNothingIsCreated()
    {
        _trackers.Setup(t => t.GetAllDefinitionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackerDefinitionEntity { Name = "Omnipod DASH pod" }]);

        await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        _broadcast.Verify(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ANewTrackerRecordsTheRegisteringMemberAsItsCreator()
    {
        await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        _created.Single().CreatedBy.Should().Be(UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-in-the-catalog")]
    [InlineData("custom-pump")]
    public async Task ADeviceWithoutCatalogWearDataGetsNoTrackers(string? catalogId)
    {
        var created = await _provisioner.ProvisionAsync(
            new PatientDevice { CatalogId = catalogId, DeviceCategory = DeviceCategory.InsulinPump }, UserId);

        created.Should().Be(0);
        _trackers.Verify(t => t.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AProvisionedTracker_HasNoThresholdsAndNoRules()
    {
        await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        _created.Single().NotificationThresholds.Should().BeEmpty();
        _ruleSync.VerifyNoOtherCalls();
    }


    private TrackerDefinitionEntity Existing(ConsumableTrackerSpec spec)
    {
        var definition = ConsumableTrackerSpecs.ToDefinition(spec, "another-member", withThresholds: false);
        definition.Id = Guid.NewGuid();
        _existing.Add(definition);
        return definition;
    }

    private static ConsumableTrackerSpec SpecOf(string catalogId, TrackerCategory category) =>
        ConsumableTrackerSpecs.SpecsFor(DeviceCatalog.GetById(catalogId)!).Single(s => s.Category == category);

    private void ShouldHaveChangedNothing(int changed)
    {
        changed.Should().Be(0);
        _created.Should().BeEmpty();
        _trackers.Verify(t => t.UpdateDefinitionAsync(
            It.IsAny<Guid>(), It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()), Times.Never);
        _trackers.Verify(t => t.StartInstanceAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SwitchingCgmModels_KeepsTheOneSensorTrackerAsItIs()
    {
        var g6 = Existing(SpecOf("dexcom-g6", TrackerCategory.Sensor));

        var changed = await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        ShouldHaveChangedNothing(changed);
        _existing.Where(d => d.Category == TrackerCategory.Sensor).Should().ContainSingle()
            .Which.Should().BeSameAs(g6);
        g6.Name.Should().Be("Dexcom G6 sensor");
    }

    [Fact]
    public async Task ACgmRegisteredAfterAConversion_KeepsTheConvertedSensorTracker()
    {
        Existing(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Sensor));

        var changed = await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        ShouldHaveChangedNothing(changed);
    }

    [Fact]
    public async Task AMembersHandRunSensorTracker_IsLeftAloneAndNothingIsAdded()
    {
        _existing.Add(new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            Name = "My sensor",
            Category = TrackerCategory.Sensor,
            Mode = TrackerMode.Duration,
        });

        var changed = await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        ShouldHaveChangedNothing(changed);
    }

    [Fact]
    public async Task SwitchingFromATubedPumpToAPatchPump_AddsThePodTracker()
    {
        Existing(SpecOf("dana-i", TrackerCategory.Cannula));
        Existing(SpecOf("dana-i", TrackerCategory.Reservoir));

        await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        _created.Select(d => d.Name).Should().Equal("Omnipod DASH pod");
    }

    [Fact]
    public async Task SwitchingFromAPatchPumpToATubedPump_AddsTheSiteAndReservoirTrackers()
    {
        Existing(SpecOf("omnipod-dash", TrackerCategory.Pod));

        await _provisioner.ProvisionAsync(Device("dana-i", DeviceCategory.InsulinPump), UserId);

        _created.Select(d => d.Name).Should().BeEquivalentTo(["Dana-i infusion site", "Dana-i reservoir"]);
    }

    [Fact]
    public async Task ACoveringTrackerWithEveryRestartEvent_StopsAPumpSpec()
    {
        Existing(ConsumableTrackerSpecs.Generic(LegacyConsumableKind.Site));

        await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task ACommitThatReportedFailure_LandedWhenItsDefinitionAndRunExist()
    {
        Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>? verify = null;
        _trackers.Setup(t => t.ExecuteUnderConsumableLockAsync(
                It.IsAny<Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<List<CreatedConsumableTracker>>>>(),
                It.IsAny<Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .Callback((Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<List<CreatedConsumableTracker>>> _,
                Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>? v,
                CancellationToken _) => verify = v)
            .ReturnsAsync([]);
        await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        var definition = new TrackerDefinitionEntity { Id = Guid.NewGuid() };
        var run = new TrackerInstanceEntity { Id = Guid.NewGuid() };
        var spec = SpecOf("dexcom-g7", TrackerCategory.Sensor);
        var stored = new Mock<ITrackerRepository>();
        stored.Setup(t => t.GetDefinitionByIdAsync(definition.Id, It.IsAny<CancellationToken>())).ReturnsAsync(definition);

        (await verify!(stored.Object, [new(definition, spec, null, null)], default)).Should().BeTrue();
        (await verify(stored.Object, [new(definition, spec, run, null)], default)).Should().BeFalse();
        stored.Setup(t => t.GetInstanceByIdAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(run);
        (await verify(stored.Object, [new(definition, spec, run, null)], default)).Should().BeTrue();
        (await verify(new Mock<ITrackerRepository>().Object, [new(definition, spec, null, null)], default)).Should().BeFalse();
    }

    [Fact]
    public async Task AFailureAfterTheCommit_IsLoggedAndTheTrackerCounts()
    {
        _broadcast.Setup(b => b.BroadcastTrackerDefinitionUpdateAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new DbUpdateException("lost"));

        var created = await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        created.Should().Be(1);
    }

    [Fact]
    public async Task AStoreFailure_IsLoggedAndCreatesNothing()
    {
        _trackers.Setup(t => t.ExecuteUnderConsumableLockAsync(
                It.IsAny<Func<ITrackerRepository, DbUnitOfWork, CancellationToken, Task<List<CreatedConsumableTracker>>>>(),
                It.IsAny<Func<ITrackerRepository, List<CreatedConsumableTracker>, CancellationToken, Task<bool>>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RetryLimitExceededException("retries exhausted", new TimeoutException()));

        var created = await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        created.Should().Be(0);
        _broadcast.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheFirstRunStartsAtTheLatestRestartingEvent()
    {
        var latest = new DeviceEvent
        {
            Id = Guid.NewGuid(), EventType = DeviceEventType.SensorStart, Timestamp = DateTime.UtcNow.AddDays(-2),
        };
        _deviceEvents.Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest);

        await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        var run = _started.Single();
        run.StartTreatmentId.Should().Be(latest.Id.ToString());
        run.StartedAt.Should().Be(latest.Timestamp);
    }

    [Fact]
    public async Task AStaleRestartingEvent_StartsNoRun()
    {
        _deviceEvents.Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent
            {
                Id = Guid.NewGuid(), EventType = DeviceEventType.SensorStart, Timestamp = DateTime.UtcNow.AddDays(-60),
            });

        await _provisioner.ProvisionAsync(Device("dexcom-g7", DeviceCategory.CGM), UserId);

        _started.Should().BeEmpty();
    }

    [Fact]
    public void EveryCatalogPumpAndCgmWithWearDataCallsForATracker()
    {
        foreach (var entry in DeviceCatalog.GetAll().Where(e => e.Cgm is not null || e.Pump is not null))
            ConsumableTrackerSpecs.SpecsFor(entry).Should().NotBeEmpty(entry.Id);
    }
}
