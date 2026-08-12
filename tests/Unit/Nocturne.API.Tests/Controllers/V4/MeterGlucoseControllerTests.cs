using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class MeterGlucoseControllerTests
{
    private readonly Mock<IMeterGlucoseRepository> _repoMock = new();
    private readonly Mock<IPatientDeviceRepository> _patientDevicesMock = new();
    private readonly Mock<IPatientDeviceStamper> _deviceStamperMock = new();

    private MeterGlucoseController CreateController()
    {
        var controller = new MeterGlucoseController(
            _repoMock.Object,
            _patientDevicesMock.Object,
            _deviceStamperMock.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task Update_PreservesExistingPatientDeviceId()
    {
        var patientDeviceId = Guid.NewGuid();
        var id = Guid.NewGuid();
        MeterGlucose? captured = null;

        SetupExisting(id, patientDeviceId);
        CaptureUpdate(id, m => captured = m);

        await CreateController().Update(id, new UpsertMeterGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 95 });

        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().Be(patientDeviceId);
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_StampsWithGlucoseMeterCategory_WhenRequestOmitsPatientDeviceId()
    {
        MeterGlucose? persisted = null;
        CaptureCreate(m => persisted = m);

        await CreateController().Create(new UpsertMeterGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 });

        persisted.Should().NotBeNull();
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.Is<IReadOnlyList<DeviceCategory>>(c => c.Contains(DeviceCategory.GlucoseMeter)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_PersistsExplicitPatientDeviceId_WithoutStamping()
    {
        var patientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(patientDeviceId);
        MeterGlucose? persisted = null;
        CaptureCreate(m => persisted = m);

        var result = await CreateController().Create(new UpsertMeterGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120,
            PatientDeviceId = patientDeviceId,
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted.Should().NotBeNull();
        persisted!.PatientDeviceId.Should().Be(patientDeviceId);
    }

    [Fact]
    public async Task Create_Returns400_WhenPatientDeviceIdDoesNotResolve()
    {
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice?)null);

        var result = await CreateController().Create(new UpsertMeterGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120,
            PatientDeviceId = Guid.NewGuid(),
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<MeterGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        MeterGlucose? persisted = null;
        CaptureCreate(m => persisted = m);

        var result = await CreateController().Create(new UpsertMeterGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120,
            PatientDeviceId = Guid.Empty,
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted.Should().NotBeNull();
        persisted!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    [Fact]
    public async Task Update_RelinksAttribution_WhenRequestCarriesPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var newPatientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(newPatientDeviceId);
        MeterGlucose? updated = null;

        SetupExisting(id, Guid.NewGuid());
        CaptureUpdate(id, m => updated = m);

        await CreateController().Update(id, new UpsertMeterGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 95,
            PatientDeviceId = newPatientDeviceId,
        });

        updated.Should().NotBeNull();
        updated!.PatientDeviceId.Should().Be(newPatientDeviceId);
    }

    [Fact]
    public async Task Update_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        var id = Guid.NewGuid();
        MeterGlucose? updated = null;

        SetupExisting(id, Guid.NewGuid());
        CaptureUpdate(id, m => updated = m);

        var result = await CreateController().Update(id, new UpsertMeterGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 95,
            PatientDeviceId = Guid.Empty,
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        updated.Should().NotBeNull();
        updated!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    private void SetupExisting(Guid id, Guid? patientDeviceId) =>
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeterGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 120, PatientDeviceId = patientDeviceId });

    private void CaptureCreate(Action<MeterGlucose> onCreate) =>
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<MeterGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<MeterGlucose, WriteOrigin, CancellationToken>((m, _, _) => onCreate(m))
            .ReturnsAsync((MeterGlucose m, WriteOrigin _, CancellationToken _) => m);

    private void CaptureUpdate(Guid id, Action<MeterGlucose> onUpdate) =>
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<MeterGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, MeterGlucose, WriteOrigin, CancellationToken>((_, m, _, _) => onUpdate(m))
            .ReturnsAsync((Guid _, MeterGlucose m, WriteOrigin _, CancellationToken _) => m);

    private void SetupRegisteredDevice(Guid patientDeviceId) =>
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(patientDeviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientDevice { Id = patientDeviceId, DeviceCategory = DeviceCategory.GlucoseMeter });

    private void VerifyStamperNeverRan() =>
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
}
