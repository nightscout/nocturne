using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class SensorGlucoseControllerTests
{
    private readonly Mock<ISensorGlucoseRepository> _repoMock = new();
    private readonly Mock<IGlucoseProcessingResolver> _glucoseResolverMock = new();
    private readonly Mock<ICanonicalAlertEvaluator> _alertEvaluatorMock = new();
    private readonly Mock<IPatientDeviceRepository> _patientDevicesMock = new();
    private readonly Mock<IPatientDeviceStamper> _deviceStamperMock = new();
    private readonly Mock<ILogger<SensorGlucoseController>> _loggerMock = new();

    private SensorGlucoseController CreateController()
    {
        var controller = new SensorGlucoseController(
            _repoMock.Object,
            _glucoseResolverMock.Object,
            _alertEvaluatorMock.Object,
            _patientDevicesMock.Object,
            _deviceStamperMock.Object,
            _loggerMock.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    [Fact]
    public async Task Create_Returns201_WhenSuccessful()
    {
        // Arrange
        var input = new UpsertSensorGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120
        };

        var created = new SensorGlucose
        {
            Id = Guid.NewGuid(),
            Timestamp = input.Timestamp.UtcDateTime,
            Mgdl = 120
        };

        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        _repoMock.As<IV4Repository<SensorGlucose>>()
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        var result = await controller.Create(input);

        // Assert
        var createdResult = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        createdResult.Value.Should().Be(created);
    }

    [Fact]
    public async Task CreateBulk_Returns201_WithCreatedReadings()
    {
        // Arrange
        var requests = new[]
        {
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 },
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5), Mgdl = 115 },
        };
        var created = new[]
        {
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = requests[0].Timestamp.UtcDateTime, Mgdl = 120 },
            new SensorGlucose { Id = Guid.NewGuid(), Timestamp = requests[1].Timestamp.UtcDateTime, Mgdl = 115 },
        };

        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var controller = CreateController();

        // Act
        var result = await controller.CreateSensorGlucoseBulk(requests);

        // Assert
        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().BeEquivalentTo(created);
    }

    [Fact]
    public async Task Update_Returns200_WithUpdatedReading()
    {
        // Arrange
        var id = Guid.NewGuid();
        var input = new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 95 };
        var existing = new SensorGlucose { Id = id, Timestamp = input.Timestamp.UtcDateTime, Mgdl = 120 };
        var updated = new SensorGlucose { Id = id, Timestamp = input.Timestamp.UtcDateTime, Mgdl = 95 };

        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updated);

        var controller = CreateController();

        // Act
        var result = await controller.Update(id, input);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(updated);
    }

    [Fact]
    public async Task Create_StampsWithCgmCategory_AndPersistsAttribution()
    {
        var deviceId = Guid.NewGuid();
        var input = new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 };

        // Stamper attributes the model in place before it reaches the repository.
        _deviceStamperMock
            .Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
                It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) => records[0].PatientDeviceId = deviceId)
            .Returns(Task.CompletedTask);

        SensorGlucose? persisted = null;
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<SensorGlucose, WriteOrigin, CancellationToken>((m, _, _) => persisted = m)
            .ReturnsAsync((SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        var controller = CreateController();

        await controller.Create(input);

        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.Is<IReadOnlyList<DeviceCategory>>(c => c.Contains(DeviceCategory.CGM)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        persisted.Should().NotBeNull();
        persisted!.PatientDeviceId.Should().Be(deviceId);
    }

    [Fact]
    public async Task CreateBulk_StampsWithCgmCategory_AndPersistsAttribution()
    {
        var deviceId = Guid.NewGuid();
        var requests = new[]
        {
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120 },
        };

        _deviceStamperMock
            .Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
                It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) => records[0].PatientDeviceId = deviceId)
            .Returns(Task.CompletedTask);

        IEnumerable<SensorGlucose>? persisted = null;
        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, WriteOrigin, CancellationToken>((m, _, _) => persisted = m.ToList())
            .ReturnsAsync((IEnumerable<SensorGlucose> m, WriteOrigin _, CancellationToken _) => m);

        var controller = CreateController();

        await controller.CreateSensorGlucoseBulk(requests);

        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.Is<IReadOnlyList<DeviceCategory>>(c => c.Contains(DeviceCategory.CGM)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        persisted.Should().NotBeNull();
        persisted!.Single().PatientDeviceId.Should().Be(deviceId);
    }

    private void SetupRegisteredDevice(Guid patientDeviceId) =>
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(patientDeviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientDevice { Id = patientDeviceId, DeviceCategory = DeviceCategory.CGM });

    private void VerifyStamperNeverRan() =>
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task Create_PersistsExplicitPatientDeviceId_WithoutStamping()
    {
        var patientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(patientDeviceId);
        SensorGlucose? persisted = null;
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<SensorGlucose, WriteOrigin, CancellationToken>((m, _, _) => persisted = m)
            .ReturnsAsync((SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        var result = await CreateController().Create(new UpsertSensorGlucoseRequest
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

        var result = await CreateController().Create(new UpsertSensorGlucoseRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            Mgdl = 120,
            PatientDeviceId = Guid.NewGuid(),
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        SensorGlucose? persisted = null;
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<SensorGlucose, WriteOrigin, CancellationToken>((m, _, _) => persisted = m)
            .ReturnsAsync((SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        var result = await CreateController().Create(new UpsertSensorGlucoseRequest
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
    public async Task Update_PreservesAttribution_AndStamps_WhenRequestOmitsPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var existingDeviceId = Guid.NewGuid();
        SensorGlucose? updated = null;
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 120, PatientDeviceId = existingDeviceId });
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SensorGlucose, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        await CreateController().Update(id, new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 95 });

        updated.Should().NotBeNull();
        updated!.PatientDeviceId.Should().Be(existingDeviceId);
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_RelinksAttribution_WhenRequestCarriesPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var newPatientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(newPatientDeviceId);
        SensorGlucose? updated = null;
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 120, PatientDeviceId = Guid.NewGuid() });
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SensorGlucose, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        await CreateController().Update(id, new UpsertSensorGlucoseRequest
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
        SensorGlucose? updated = null;
        _repoMock
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 120, PatientDeviceId = Guid.NewGuid() });
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<SensorGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SensorGlucose, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, SensorGlucose m, WriteOrigin _, CancellationToken _) => m);

        var result = await CreateController().Update(id, new UpsertSensorGlucoseRequest
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

    [Fact]
    public async Task CreateBulk_StampsOnlyTheReadingsThatDidNotClearAttribution()
    {
        var stamped = Guid.NewGuid();
        var requests = new[]
        {
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 120, PatientDeviceId = Guid.Empty },
            new UpsertSensorGlucoseRequest { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5), Mgdl = 115 },
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

        IEnumerable<SensorGlucose>? persisted = null;
        _repoMock
            .Setup(r => r.BulkCreateAsync(It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<SensorGlucose>, WriteOrigin, CancellationToken>((m, _, _) => persisted = m.ToList())
            .ReturnsAsync((IEnumerable<SensorGlucose> m, WriteOrigin _, CancellationToken _) => m);

        await CreateController().CreateSensorGlucoseBulk(requests);

        persisted.Should().NotBeNull();
        persisted!.Should().SatisfyRespectively(
            cleared => cleared.PatientDeviceId.Should().BeNull(),
            attributed => attributed.PatientDeviceId.Should().Be(stamped));
    }
}
