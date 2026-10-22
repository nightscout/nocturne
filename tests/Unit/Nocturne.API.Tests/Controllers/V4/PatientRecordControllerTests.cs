using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
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
    private readonly Mock<ISensorSessionInferenceRunner> _sessionInference = new();
    private readonly Mock<ISensorSessionInferenceService> _inference = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<ITenantCache> _tenantCache = new();
    private readonly Mock<IDeviceTrackerProvisioner> _trackerProvisioner = new();
    private readonly PatientRecordController _controller;

    private static readonly TenantContext Tenant = new(Guid.NewGuid(), "sleepy", "Sleepy", true, IsDemo: false);

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
            _reattribution.Object,
            _sessionInference.Object,
            _trackerProvisioner.Object,
            _tenantAccessor.Object,
            _tenantCache.Object,
            TempBasalTestDeps.NoReresolution(),
            NullLogger<PatientRecordController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _tenantAccessor.Setup(t => t.Context).Returns(Tenant);
        _sessionInference
            .Setup(r => r.RunAsync(
                It.IsAny<TenantContext>(), It.IsAny<Func<ISensorSessionInferenceService, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((TenantContext _, Func<ISensorSessionInferenceService, CancellationToken, Task> work, CancellationToken ct)
                => work(_inference.Object, ct));
        _deviceRepo
            .Setup(x => x.UpdateAsync(It.IsAny<Guid>(), It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, PatientDevice m, WriteOrigin _, CancellationToken _) => m);
    }

    private void GivenCreateEchoes() =>
        _deviceRepo
            .Setup(x => x.CreateAsync(It.IsAny<PatientDevice>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice m, WriteOrigin _, CancellationToken _) => m);

    private static PatientDevice Cgm() => new()
    {
        Id = Guid.NewGuid(), DeviceCategory = DeviceCategory.CGM, Manufacturer = "Dexcom", Model = "G7", CatalogId = "dexcom-g7",
    };

    [Fact]
    public async Task CreateDevice_ForACgm_InfersItsSensorSessionHistoryForTheTenant()
    {
        GivenCreateEchoes();
        var device = Cgm();

        await _controller.CreateDevice(device);

        _sessionInference.Verify(r => r.RunAsync(
            Tenant, It.IsAny<Func<ISensorSessionInferenceService, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        _inference.Verify(i => i.InferHistoryAsync(device, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void GivenStoredRecord(string? timezone) =>
        _recordRepo.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PatientRecord { Timezone = timezone });

    private void GivenUpdateEchoes() =>
        _recordRepo
            .Setup(x => x.UpdateAsync(It.IsAny<PatientRecord>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRecord m, WriteOrigin _, CancellationToken _) => m);

    [Theory]
    [InlineData("Australia/Sydney", "Europe/London")]
    [InlineData(null, "Europe/London")]
    [InlineData("Australia/Sydney", null)]
    public async Task UpdatePatientRecord_ChangingTheTimezone_ClearsTheWholeEHbA1cFamily(string? stored, string? edited)
    {
        GivenStoredRecord(stored);
        GivenUpdateEchoes();

        await _controller.UpdatePatientRecord(new PatientRecord { Timezone = edited });

        _tenantCache.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.EHbA1c, It.IsAny<CancellationToken>()), Times.Once);
        _tenantCache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData(null)]
    public async Task UpdatePatientRecord_KeepingTheTimezone_ClearsNothing(string? timezone)
    {
        GivenStoredRecord(timezone);
        GivenUpdateEchoes();

        await _controller.UpdatePatientRecord(new PatientRecord { Timezone = timezone });

        _tenantCache.VerifyNoOtherCalls();
    }

    private void GivenStored(PatientDevice device) =>
        _deviceRepo.Setup(x => x.GetByIdAsync(device.Id, It.IsAny<CancellationToken>())).ReturnsAsync(device);

    [Fact]
    public async Task UpdateDevice_ChangingACgmsDates_ReattributesAndInfersItsHistory()
    {
        var stored = Cgm();
        GivenStored(stored);
        var edited = Cgm();
        edited.Id = stored.Id;
        edited.StartDate = new DateOnly(2026, 9, 1);

        await _controller.UpdateDevice(edited.Id, edited);

        _reattribution.Verify(r => r.ReattributeForDeviceAsync(edited, It.IsAny<CancellationToken>()), Times.Once);
        _inference.Verify(i => i.InferHistoryAsync(edited, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Its inferred starts go first: once the registration is gone they name no device.</summary>
    [Fact]
    public async Task DeleteDevice_ForACgm_ForgetsItsInferredStartsBeforeDeleting()
    {
        var stored = Cgm();
        GivenStored(stored);
        var order = new List<string>();
        _inference
            .Setup(i => i.ForgetDeviceAsync(stored, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("forget"))
            .Returns(Task.CompletedTask);
        _deviceRepo
            .Setup(x => x.DeleteAsync(stored.Id, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("delete"))
            .Returns(Task.CompletedTask);

        await _controller.DeleteDevice(stored.Id);

        order.Should().Equal("forget", "delete");
    }

    [Fact]
    public async Task UpdateDevice_RenamingACgm_RedoesNothing()
    {
        var stored = Cgm();
        GivenStored(stored);
        var edited = Cgm();
        edited.Id = stored.Id;
        edited.Notes = "left arm";

        await _controller.UpdateDevice(edited.Id, edited);

        _reattribution.VerifyNoOtherCalls();
        _sessionInference.VerifyNoOtherCalls();
    }

    /// <summary>A CGM edited into another category keeps no inferred starts, so its history is still redone.</summary>
    [Fact]
    public async Task UpdateDevice_TurningACgmIntoAMeter_RedoesItsHistory()
    {
        var stored = Cgm();
        GivenStored(stored);
        var edited = Cgm();
        edited.Id = stored.Id;
        edited.DeviceCategory = DeviceCategory.GlucoseMeter;

        await _controller.UpdateDevice(edited.Id, edited);

        _inference.Verify(i => i.InferHistoryAsync(edited, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDevice_ForAPump_InfersNoSensorSessions()
    {
        GivenCreateEchoes();

        await _controller.CreateDevice(new PatientDevice { DeviceCategory = DeviceCategory.InsulinPump, Manufacturer = "Tandem", Model = "t:slim X2" });

        _sessionInference.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateDevice_ProvisionsTheDevicesTrackersForTheCaller()
    {
        GivenCreateEchoes();
        var subjectId = Guid.NewGuid();
        _controller.HttpContext.Items[AuthContextKeys.AuthContext] =
            new AuthContext { IsAuthenticated = true, SubjectId = subjectId };
        var pump = new PatientDevice { DeviceCategory = DeviceCategory.InsulinPump, CatalogId = "omnipod-dash" };

        await _controller.CreateDevice(pump);

        _trackerProvisioner.Verify(
            p => p.ProvisionAsync(pump, subjectId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateDevice_WithoutASubject_ProvisionsNoTrackers()
    {
        GivenCreateEchoes();

        await _controller.CreateDevice(new PatientDevice { DeviceCategory = DeviceCategory.InsulinPump, CatalogId = "omnipod-dash" });

        _trackerProvisioner.VerifyNoOtherCalls();
    }

    /// <summary>The device is already saved, so the request still succeeds.</summary>
    [Fact]
    public async Task CreateDevice_WhenInferringHistoryFails_StillReturnsTheCreatedDevice()
    {
        GivenCreateEchoes();
        _inference
            .Setup(i => i.InferHistoryAsync(It.IsAny<PatientDevice>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var result = await _controller.CreateDevice(Cgm());

        result.Result.Should().BeOfType<CreatedAtActionResult>();
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
