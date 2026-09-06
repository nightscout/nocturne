using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Projections;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

public class PatientRecordControllerTests
{
    private readonly Mock<IPatientRecordRepository> _recordRepo;
    private readonly Mock<IPatientDeviceRepository> _deviceRepo;
    private readonly Mock<IPatientInsulinRepository> _insulinRepo;
    private readonly Mock<IDeviceService> _deviceService;
    private readonly Mock<ISensorGlucoseRepository> _sensorGlucoseRepo;
    private readonly Mock<IDeviceReattributionService> _reattribution;
    private readonly PatientRecordController _controller;

    public PatientRecordControllerTests()
    {
        _recordRepo = new Mock<IPatientRecordRepository>();
        _deviceRepo = new Mock<IPatientDeviceRepository>();
        _insulinRepo = new Mock<IPatientInsulinRepository>();
        _deviceService = new Mock<IDeviceService>();
        _sensorGlucoseRepo = new Mock<ISensorGlucoseRepository>();
        _reattribution = new Mock<IDeviceReattributionService>();

        _controller = new PatientRecordController(
            _recordRepo.Object,
            _deviceRepo.Object,
            _insulinRepo.Object,
            _deviceService.Object,
            _sensorGlucoseRepo.Object,
            _reattribution.Object);
    }

    [Fact]
    public async Task CreateDevice_WithSerialNumber_ResolvesDeviceId()
    {
        // Arrange
        var expectedDeviceId = Guid.NewGuid();
        var model = new PatientDevice
        {
            DeviceCategory = DeviceCategory.InsulinPump,
            Manufacturer = "Medtronic",
            Model = "780G",
            SerialNumber = "ABC123",
        };

        _deviceService
            .Setup(x => x.ResolveAsync(
                DeviceCategory.InsulinPump,
                "Medtronic 780G",
                "ABC123",
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        _deviceRepo
            .Setup(x => x.CreateAsync(It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice m, WriteOrigin origin, CancellationToken _) => m);

        // Act
        var result = await _controller.CreateDevice(model);

        // Assert
        _deviceService.Verify(x => x.ResolveAsync(
            DeviceCategory.InsulinPump,
            "Medtronic 780G",
            "ABC123",
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var device = createdResult.Value.Should().BeOfType<PatientDevice>().Subject;
        device.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task CreateDevice_WithoutSerialNumber_DoesNotResolveDeviceId()
    {
        // Arrange
        var model = new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM,
            Manufacturer = "Dexcom",
            Model = "G7",
            SerialNumber = null,
        };

        _deviceRepo
            .Setup(x => x.CreateAsync(It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice m, WriteOrigin origin, CancellationToken _) => m);

        // Act
        await _controller.CreateDevice(model);

        // Assert
        _deviceService.Verify(x => x.ResolveAsync(
            It.IsAny<DeviceCategory>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateDevice_WithSerialNumber_ResolvesDeviceId()
    {
        // Arrange
        var deviceId = Guid.NewGuid();
        var expectedDeviceId = Guid.NewGuid();
        var model = new PatientDevice
        {
            DeviceCategory = DeviceCategory.InsulinPump,
            Manufacturer = "Tandem",
            Model = "t:slim X2",
            SerialNumber = "XYZ789",
        };

        _deviceService
            .Setup(x => x.ResolveAsync(
                DeviceCategory.InsulinPump,
                "Tandem t:slim X2",
                "XYZ789",
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDeviceId);

        _deviceRepo
            .Setup(x => x.UpdateAsync(deviceId, It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, PatientDevice m, WriteOrigin origin, CancellationToken _) => m);

        // Act
        var result = await _controller.UpdateDevice(deviceId, model);

        // Assert
        _deviceService.Verify(x => x.ResolveAsync(
            DeviceCategory.InsulinPump,
            "Tandem t:slim X2",
            "XYZ789",
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var device = okResult.Value.Should().BeOfType<PatientDevice>().Subject;
        device.DeviceId.Should().Be(expectedDeviceId);
    }

    [Fact]
    public async Task CreateDevice_WithWhitespaceSerialNumber_DoesNotResolveDeviceId()
    {
        // Arrange
        var model = new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM,
            Manufacturer = "Abbott",
            Model = "Libre 3",
            SerialNumber = "   ",
        };

        _deviceRepo
            .Setup(x => x.CreateAsync(It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice m, WriteOrigin origin, CancellationToken _) => m);

        // Act
        await _controller.CreateDevice(model);

        // Assert
        _deviceService.Verify(x => x.ResolveAsync(
            It.IsAny<DeviceCategory>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDiscoveredSources_ReturnsRepositoryResults()
    {
        var discovered = new List<DiscoveredSource>
        {
            new("xdrip", "DexcomG6", 42, DateTime.UtcNow),
        };
        _sensorGlucoseRepo
            .Setup(r => r.GetDiscoveredSourcesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(discovered);

        var result = await _controller.GetDiscoveredSources();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(discovered);
    }

    [Fact]
    public async Task CreateDevice_ReattributesTheCreatedDevice()
    {
        var model = new PatientDevice
        {
            DeviceCategory = DeviceCategory.CGM,
            Manufacturer = "Dexcom",
            Model = "G7",
        };
        PatientDevice? created = null;
        _deviceRepo
            .Setup(x => x.CreateAsync(It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice m, WriteOrigin _, CancellationToken _) => { created = m; return m; });

        await _controller.CreateDevice(model);

        _reattribution.Verify(
            r => r.ReattributeForDeviceAsync(created!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReorderDevices_MapsAssignmentsToRepository()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var assignments = new List<DeviceRankAssignment>
        {
            new(a, 0),
            new(b, 1),
        };
        var changed = new List<PatientDevice> { new() { Id = a, Rank = 0 } };
        IReadOnlyList<(Guid Id, int Rank)>? captured = null;
        _deviceRepo
            .Setup(x => x.ReorderAsync(It.IsAny<IReadOnlyList<(Guid, int)>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<(Guid, int)>, WriteOrigin, CancellationToken>((r, _, _) => captured = r)
            .ReturnsAsync(changed);

        var result = await _controller.ReorderDevices(assignments);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(changed);
        captured.Should().NotBeNull();
        captured.Should().Contain((a, 0));
        captured.Should().Contain((b, 1));
    }
}
