using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Devices;

public class DeviceAgeServiceTests
{
    private readonly Mock<IDeviceEventRepository> _repositoryMock;
    private readonly DeviceAgeService _service;

    public DeviceAgeServiceTests()
    {
        _repositoryMock = new Mock<IDeviceEventRepository>();
        _service = new DeviceAgeService(
            _repositoryMock.Object,
            NullLogger<DeviceAgeService>.Instance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCannulaAgeAsync_WithRecentSiteChange_ReturnsCorrectAge()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-26);
        var deviceEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = eventTime,
            EventType = DeviceEventType.SiteChange,
            Notes = "Changed site"
        };

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types =>
                    types.Contains(DeviceEventType.SiteChange) &&
                    types.Contains(DeviceEventType.CannulaChange)),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceEvent);

        var prefs = new DeviceAgePreferences();

        // Act
        var result = await _service.GetCannulaAgeAsync(prefs);

        // Assert
        result.Found.Should().BeTrue();
        result.Age.Should().Be(26);
        result.Days.Should().Be(1);
        result.Hours.Should().Be(2);
        result.Notes.Should().Be("Changed site");
        result.Display.Should().Be("26h");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCannulaAgeAsync_NoEvents_ReturnsNotFound()
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEvent?)null);

        var prefs = new DeviceAgePreferences();

        // Act
        var result = await _service.GetCannulaAgeAsync(prefs);

        // Assert
        result.Found.Should().BeFalse();
        result.Age.Should().Be(0);
        result.Days.Should().Be(0);
        result.Hours.Should().Be(0);
        result.Display.Should().Be("n/a");
        result.Level.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSensorAgeAsync_WithSensorStartAndChange_ReturnsCorrectAge()
    {
        // Arrange
        var sensorStartTime = DateTime.UtcNow.AddHours(-48);
        var sensorChangeTime = DateTime.UtcNow.AddHours(-24);

        var sensorStartEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = sensorStartTime,
            EventType = DeviceEventType.SensorStart
        };

        var sensorChangeEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = sensorChangeTime,
            EventType = DeviceEventType.SensorChange
        };

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types => types.Contains(DeviceEventType.SensorStart)),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sensorStartEvent);

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types => types.Contains(DeviceEventType.SensorChange)),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(sensorChangeEvent);

        var prefs = new DeviceAgePreferences();

        // Act
        var result = await _service.GetSensorAgeAsync(prefs);

        // Assert
        result.SensorChange.Found.Should().BeTrue();
        result.SensorChange.Age.Should().Be(24);
        result.Min.Should().Be("Sensor Change"); // Change is more recent, so it's the min
        result.SensorStart.Found.Should().BeFalse(); // Legacy behavior: hidden when Change is more recent
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetInsulinAgeAsync_WithRecentInsulinChange_ReturnsCorrectAge()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-12);
        var deviceEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = eventTime,
            EventType = DeviceEventType.InsulinChange,
            Notes = "New reservoir"
        };

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types =>
                    types.Contains(DeviceEventType.InsulinChange) &&
                    types.Contains(DeviceEventType.ReservoirChange)),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceEvent);

        var prefs = new DeviceAgePreferences();

        // Act
        var result = await _service.GetInsulinAgeAsync(prefs);

        // Assert
        result.Found.Should().BeTrue();
        result.Age.Should().Be(12);
        result.Days.Should().Be(0);
        result.Hours.Should().Be(12);
        result.Notes.Should().Be("New reservoir");
        result.Level.Should().Be(0); // 12h is below default info threshold of 44h
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBatteryAgeAsync_WithRecentBatteryChange_ReturnsCorrectAge()
    {
        // Arrange
        var eventTime = DateTime.UtcNow.AddHours(-340);
        var deviceEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = eventTime,
            EventType = DeviceEventType.PumpBatteryChange
        };

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types =>
                    types.Contains(DeviceEventType.PumpBatteryChange)),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deviceEvent);

        var prefs = new DeviceAgePreferences();

        // Act
        var result = await _service.GetBatteryAgeAsync(prefs);

        // Assert
        result.Found.Should().BeTrue();
        result.Age.Should().Be(340);
        result.Days.Should().Be(14);
        result.Hours.Should().Be(4);
        result.Level.Should().Be(1); // 340h is above warn (336) but below urgent (360)
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCannulaAgeAsync_WithPatientDeviceId_UsesThatDevicesEvent()
    {
        // Arrange
        var pumpId = Guid.NewGuid();
        var scopedEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow.AddHours(-30),
            EventType = DeviceEventType.SiteChange,
            PatientDeviceId = pumpId
        };
        var otherPumpEvent = new DeviceEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow.AddHours(-2),
            EventType = DeviceEventType.SiteChange,
            PatientDeviceId = Guid.NewGuid()
        };

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(otherPumpEvent);

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                pumpId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(scopedEvent);

        // Act
        var result = await _service.GetCannulaAgeAsync(new DeviceAgePreferences(), pumpId);

        // Assert
        result.Age.Should().Be(30);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSensorAgeAsync_WithPatientDeviceId_ScopesBothEventTypeLookups()
    {
        // Arrange
        var sensorId = Guid.NewGuid();

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow.AddHours(-2),
                EventType = DeviceEventType.SensorChange,
                PatientDeviceId = Guid.NewGuid()
            });

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types => types.Contains(DeviceEventType.SensorStart)),
                sensorId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow.AddHours(-100),
                EventType = DeviceEventType.SensorStart,
                PatientDeviceId = sensorId
            });

        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.Is<DeviceEventType[]>(types => types.Contains(DeviceEventType.SensorChange)),
                sensorId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEvent?)null);

        // Act
        var result = await _service.GetSensorAgeAsync(new DeviceAgePreferences(), sensorId);

        // Assert
        result.Min.Should().Be("Sensor Start");
        result.SensorStart.Found.Should().BeTrue();
        result.SensorStart.Age.Should().Be(100);
        result.SensorChange.Found.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetBatteryAgeAsync_WithNoPatientDeviceId_QueriesTenantWide()
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeviceEvent?)null);

        // Act
        await _service.GetBatteryAgeAsync(new DeviceAgePreferences());

        // Assert
        _repositoryMock.Verify(
            r => r.GetLatestByEventTypesAsync(
                It.IsAny<DeviceEventType[]>(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
