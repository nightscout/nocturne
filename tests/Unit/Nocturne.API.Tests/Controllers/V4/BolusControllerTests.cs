using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class BolusControllerTests
{
    private readonly Mock<IBolusRepository> _repoMock = new();
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock = new();
    private readonly Mock<IPatientDeviceRepository> _patientDevicesMock = new();
    private readonly Mock<IPatientDeviceStamper> _deviceStamperMock = new();

    private BolusController CreateController()
    {
        var controller = new BolusController(
            _repoMock.Object,
            _insulinRepoMock.Object,
            _patientDevicesMock.Object,
            _deviceStamperMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private void SetupCreatePassthrough(Action<Bolus> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Bolus, WriteOrigin, CancellationToken>((b, _, _) => onCreate(b))
            .ReturnsAsync((Bolus b, WriteOrigin origin, CancellationToken _) => b);
    }

    [Fact]
    public async Task Create_PassesThroughCorrelationId()
    {
        var cid = Guid.NewGuid();
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            CorrelationId = cid,
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(cid);
    }

    [Fact]
    public async Task Update_RequestCorrelationIdWins_WhenSupplied()
    {
        var existingCid = Guid.NewGuid();
        var requestCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new Bolus
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Insulin = 2.0,
            CorrelationId = existingCid,
        };
        Bolus? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Bolus, WriteOrigin, CancellationToken>((_, b, _, _) => captured = b)
            .ReturnsAsync((Guid _, Bolus b, WriteOrigin origin, CancellationToken _) => b);

        var controller = CreateController();
        var request = new UpdateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 3.0,
            CorrelationId = requestCid,
        };

        await controller.Update(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(requestCid);
    }

    [Fact]
    public async Task Create_WithoutCorrelationId_ServerMintsNonEmptyGuid()
    {
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            // CorrelationId intentionally omitted
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Update_PreservesExistingCorrelationId_WhenRequestOmits()
    {
        var existingCid = Guid.NewGuid();
        var id = Guid.NewGuid();
        var existing = new Bolus
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            Insulin = 2.0,
            CorrelationId = existingCid,
        };
        Bolus? captured = null;

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Bolus, WriteOrigin, CancellationToken>((_, b, _, _) => captured = b)
            .ReturnsAsync((Guid _, Bolus b, WriteOrigin origin, CancellationToken _) => b);

        var controller = CreateController();
        var request = new UpdateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 3.0,
            // CorrelationId intentionally omitted
        };

        await controller.Update(id, request);

        captured.Should().NotBeNull();
        captured!.CorrelationId.Should().Be(existingCid);
    }

    [Fact]
    public async Task Update_PreservesExistingPatientDeviceId()
    {
        var patientDeviceId = Guid.NewGuid();
        var id = Guid.NewGuid();
        Bolus? captured = null;

        SetupExisting(id, patientDeviceId);
        CaptureUpdate(id, b => captured = b);

        await CreateController().Update(id, new UpdateBolusRequest { Timestamp = DateTimeOffset.UtcNow, Insulin = 3.0 });

        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().Be(patientDeviceId);
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_StampsWithInsulinDeliveryCategories_WhenRequestOmitsPatientDeviceId()
    {
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        await CreateController().Create(new CreateBolusRequest { Timestamp = DateTimeOffset.UtcNow, Insulin = 5.0 });

        captured.Should().NotBeNull();
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.Is<IReadOnlyList<DeviceCategory>>(c => c.Contains(DeviceCategory.InsulinPump) && c.Contains(DeviceCategory.SmartPen)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_PersistsExplicitPatientDeviceId_WithoutStamping()
    {
        var patientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(patientDeviceId);
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var result = await CreateController().Create(new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            PatientDeviceId = patientDeviceId,
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().Be(patientDeviceId);
    }

    [Fact]
    public async Task Create_Returns400_WhenPatientDeviceIdDoesNotResolve()
    {
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice?)null);

        var result = await CreateController().Create(new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            PatientDeviceId = Guid.NewGuid(),
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var result = await CreateController().Create(new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            PatientDeviceId = Guid.Empty,
        });

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    [Fact]
    public async Task Update_RelinksAttribution_WhenRequestCarriesPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var newPatientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(newPatientDeviceId);
        Bolus? captured = null;

        SetupExisting(id, Guid.NewGuid());
        CaptureUpdate(id, b => captured = b);

        await CreateController().Update(id, new UpdateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 3.0,
            PatientDeviceId = newPatientDeviceId,
        });

        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().Be(newPatientDeviceId);
    }

    [Fact]
    public async Task Update_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        var id = Guid.NewGuid();
        Bolus? captured = null;

        SetupExisting(id, Guid.NewGuid());
        CaptureUpdate(id, b => captured = b);

        var result = await CreateController().Update(id, new UpdateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 3.0,
            PatientDeviceId = Guid.Empty,
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    [Fact]
    public async Task CreateBulk_StampsOnlyTheBolusesThatDidNotClearAttribution()
    {
        var stamped = Guid.NewGuid();
        var requests = new[]
        {
            new CreateBolusRequest { Timestamp = DateTimeOffset.UtcNow, Insulin = 5.0, PatientDeviceId = Guid.Empty },
            new CreateBolusRequest { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5), Insulin = 2.0 },
        };

        _deviceStamperMock
            .Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
                It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) =>
                {
                    foreach (var record in records)
                        record.PatientDeviceId = stamped;
                })
            .Returns(Task.CompletedTask);

        IEnumerable<Bolus>? persisted = null;
        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<Bolus>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Bolus>, WriteOrigin, CancellationToken>((b, _, _) => persisted = b.ToList())
            .ReturnsAsync((IEnumerable<Bolus> b, WriteOrigin _, CancellationToken _) => b);

        await CreateController().CreateBolusesBulk(requests);

        persisted.Should().NotBeNull();
        persisted!.Should().SatisfyRespectively(
            cleared => cleared.PatientDeviceId.Should().BeNull(),
            attributed => attributed.PatientDeviceId.Should().Be(stamped));
    }

    private void SetupExisting(Guid id, Guid? patientDeviceId) =>
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Bolus { Id = id, Timestamp = DateTime.UtcNow, Insulin = 2.0, PatientDeviceId = patientDeviceId });

    private void CaptureUpdate(Guid id, Action<Bolus> onUpdate) =>
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<Bolus>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Bolus, WriteOrigin, CancellationToken>((_, b, _, _) => onUpdate(b))
            .ReturnsAsync((Guid _, Bolus b, WriteOrigin _, CancellationToken _) => b);

    private void SetupRegisteredDevice(Guid patientDeviceId) =>
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(patientDeviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientDevice { Id = patientDeviceId, DeviceCategory = DeviceCategory.InsulinPump });

    private void VerifyStamperNeverRan() =>
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task Create_WithPatientInsulinId_EnrichesInsulinContext()
    {
        var insulinId = Guid.NewGuid();
        var insulin = new PatientInsulin
        {
            Id = insulinId,
            Name = "Fiasp",
            Dia = 3.5,
            Peak = 55,
            Curve = "ultra-rapid",
            Concentration = 100,
        };

        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            InsulinType = "whatever",
            PatientInsulinId = insulinId,
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.InsulinContext.Should().NotBeNull();
        captured.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        captured.InsulinContext.InsulinName.Should().Be("Fiasp");
        captured.InsulinContext.Dia.Should().Be(3.5);
        captured.InsulinType.Should().Be("Fiasp"); // server overwrites
    }

    [Fact]
    public async Task Create_WithInvalidPatientInsulinId_LeavesContextNull()
    {
        var badId = Guid.NewGuid();
        _insulinRepoMock.Setup(r => r.GetByIdAsync(badId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin?)null);

        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            InsulinType = "Humalog",
            PatientInsulinId = badId,
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.InsulinContext.Should().BeNull();
        captured.InsulinType.Should().Be("Humalog"); // preserved since ID didn't resolve
    }

    [Fact]
    public async Task Create_WithoutPatientInsulinId_NoEnrichment()
    {
        Bolus? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBolusRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Insulin = 5.0,
            InsulinType = "Manual Entry",
        };

        await controller.Create(request);

        captured.Should().NotBeNull();
        captured!.InsulinContext.Should().BeNull();
        captured.InsulinType.Should().Be("Manual Entry");
        _insulinRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
