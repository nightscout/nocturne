using FluentAssertions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

/// <summary>
/// Unit tests for DeviceService — resolves device identity from (category, type, serial) tuples
/// </summary>
public class DeviceServiceTests
{
    private readonly Mock<IDeviceRepository> _mockRepository;
    private readonly Mock<IPatientDeviceRepository> _mockPatientDeviceRepository;
    private readonly DeviceService _service;

    public DeviceServiceTests()
    {
        _mockRepository = new Mock<IDeviceRepository>();
        _mockPatientDeviceRepository = new Mock<IPatientDeviceRepository>();
        _service = new DeviceService(_mockRepository.Object, _mockPatientDeviceRepository.Object, MockTenantAccessor.Create().Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullType_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, null, "ABC123", 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByCategoryTypeAndSerialAsync(It.IsAny<DeviceCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullSerial_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, "Omnipod DASH", null, 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByCategoryTypeAndSerialAsync(It.IsAny<DeviceCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_EmptyType_ReturnsNull()
    {
        // Act
        var result = await _service.ResolveAsync(DeviceCategory.InsulinPump, "", "ABC123", 1000);

        // Assert
        result.Should().BeNull();
        _mockRepository.Verify(
            r => r.FindByCategoryTypeAndSerialAsync(It.IsAny<DeviceCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NewDevice_CreatesAndReturnsId()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var mills = 1700000000000L;

        _mockRepository
            .Setup(r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device?)null);

        _mockRepository
            .Setup(r => r.CreateAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Device device, CancellationToken _) => device);

        // Act
        var result = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result.Should().NotBeNull();
        _mockRepository.Verify(
            r => r.CreateAsync(
                It.Is<Device>(d =>
                    d.Category == category &&
                    d.Type == type &&
                    d.Serial == serial &&
                    d.FirstSeenTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime &&
                    d.LastSeenTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_ReturnsExistingId()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Medtronic 780G";
        var serial = "XYZ789";
        var existingId = Guid.NewGuid();
        var mills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1699000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDevice);

        // Act
        var result = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.CreateAsync(It.IsAny<Device>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_UpdatesLastSeenMills()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var olderMills = 1699000000000L;
        var newerMills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1698000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(olderMills).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDevice);

        _mockRepository
            .Setup(r => r.UpdateAsync(existingId, It.IsAny<Device>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Device d, CancellationToken _) => d);

        // Act
        var result = await _service.ResolveAsync(category, type, serial, newerMills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.UpdateAsync(
                existingId,
                It.Is<Device>(d => d.LastSeenTimestamp == DateTimeOffset.FromUnixTimeMilliseconds(newerMills).UtcDateTime),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_ExistingDevice_DoesNotUpdateWhenMillsOlder()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var newerMills = 1700000000000L;
        var olderMills = 1699000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1698000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(newerMills).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDevice);

        // Act
        var result = await _service.ResolveAsync(category, type, serial, olderMills);

        // Assert
        result.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<Device>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_SameDeviceTwice_UsesCacheSecondTime()
    {
        // Arrange
        var category = DeviceCategory.InsulinPump;
        var type = "Omnipod DASH";
        var serial = "ABC123";
        var existingId = Guid.NewGuid();
        var mills = 1700000000000L;

        var existingDevice = new Device
        {
            Id = existingId,
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1699000000000L).UtcDateTime,
            LastSeenTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).UtcDateTime
        };

        _mockRepository
            .Setup(r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDevice);

        // Act — call twice with same category/type/serial
        var result1 = await _service.ResolveAsync(category, type, serial, mills);
        var result2 = await _service.ResolveAsync(category, type, serial, mills);

        // Assert
        result1.Should().Be(existingId);
        result2.Should().Be(existingId);
        _mockRepository.Verify(
            r => r.FindByCategoryTypeAndSerialAsync(category, type, serial, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_WithMatchingDeviceAndTimestamp_ReturnsPatientDeviceId()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = new DateOnly(2026, 6, 1),
                }
            });

        var result = await _service.ResolvePatientDeviceAsync(deviceId, mills);
        result.Should().Be(patientDeviceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_WithNullDeviceId_ReturnsNull()
    {
        var result = await _service.ResolvePatientDeviceAsync(null, 1700000000000L);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_WithNoMatchingPatientDevice_ReturnsNull()
    {
        var deviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>());

        var result = await _service.ResolvePatientDeviceAsync(deviceId, mills);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_WithTimestampOutsideDateRange_ReturnsNull()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2025-06-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = new DateOnly(2026, 6, 1),
                }
            });

        var result = await _service.ResolvePatientDeviceAsync(deviceId, mills);
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_WithNullEndDate_MatchesCurrentDevice()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2030-01-01T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = null,
                }
            });

        var result = await _service.ResolvePatientDeviceAsync(deviceId, mills);
        result.Should().Be(patientDeviceId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolvePatientDeviceAsync_SameDeviceTwice_UsesCacheSecondTime()
    {
        var deviceId = Guid.CreateVersion7();
        var patientDeviceId = Guid.CreateVersion7();
        var mills = DateTimeOffset.Parse("2026-03-15T12:00:00Z").ToUnixTimeMilliseconds();

        _mockPatientDeviceRepository
            .Setup(r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PatientDevice>
            {
                new()
                {
                    Id = patientDeviceId,
                    DeviceId = deviceId,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = null,
                }
            });

        var result1 = await _service.ResolvePatientDeviceAsync(deviceId, mills);
        var result2 = await _service.ResolvePatientDeviceAsync(deviceId, mills);

        result1.Should().Be(patientDeviceId);
        result2.Should().Be(patientDeviceId);
        _mockPatientDeviceRepository.Verify(
            r => r.GetByDeviceIdAsync(deviceId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
