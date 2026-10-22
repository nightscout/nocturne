using FluentAssertions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceTrackerProvisionerTests
{
    private const string UserId = "user-1";

    private readonly Mock<ITrackerRepository> _trackers = new();
    private readonly List<TrackerDefinitionEntity> _created = [];
    private readonly DeviceTrackerProvisioner _provisioner;

    public DeviceTrackerProvisionerTests()
    {
        _trackers.Setup(t => t.GetDefinitionsForUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _trackers.Setup(t => t.CreateDefinitionAsync(It.IsAny<TrackerDefinitionEntity>(), It.IsAny<CancellationToken>()))
            .Callback<TrackerDefinitionEntity, CancellationToken>((d, _) => _created.Add(d))
            .ReturnsAsync((TrackerDefinitionEntity d, CancellationToken _) => d);
        _provisioner = new DeviceTrackerProvisioner(_trackers.Object);
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
        pod.Category.Should().Be(TrackerCategory.Cannula);
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
    public async Task ATrackerTheUserAlreadyHasIsNotCreatedAgain()
    {
        _trackers.Setup(t => t.GetDefinitionsForUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrackerDefinitionEntity { Name = "omnipod dash POD" }]);

        var created = await _provisioner.ProvisionAsync(Device("omnipod-dash", DeviceCategory.InsulinPump), UserId);

        created.Should().Be(0);
        _created.Should().BeEmpty();
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
    public void EveryCatalogPumpAndCgmWithWearDataCallsForATracker()
    {
        foreach (var entry in DeviceCatalog.GetAll().Where(e => e.Cgm is not null || e.Pump is not null))
            DeviceTrackerProvisioner.SpecsFor(entry).Should().NotBeEmpty(entry.Id);
    }
}
