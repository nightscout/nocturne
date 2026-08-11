using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class DeviceEventControllerTests
{
    private readonly Mock<IDeviceEventRepository> _repoMock = new();
    private readonly Mock<IPatientDeviceRepository> _patientDevicesMock = new();
    private readonly Mock<IPatientDeviceStamper> _deviceStamperMock = new();

    private DeviceEventController CreateController(IQueryCollection? query = null)
    {
        var controller = new DeviceEventController(
            _repoMock.Object,
            _patientDevicesMock.Object,
            _deviceStamperMock.Object);

        var httpContext = new DefaultHttpContext();
        if (query is not null)
            httpContext.Request.Query = query;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static UpsertDeviceEventRequest ValidRequest(Guid? patientDeviceId = null, DeviceEventType eventType = DeviceEventType.SiteChange) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        EventType = eventType,
        PatientDeviceId = patientDeviceId,
    };

    private void SetupCreatePassthrough(Action<DeviceEvent>? capture = null) =>
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceEvent, WriteOrigin, CancellationToken>((m, _, _) => capture?.Invoke(m))
            .ReturnsAsync((DeviceEvent m, WriteOrigin _, CancellationToken _) => m);

    private void SetupRegisteredDevice(Guid patientDeviceId) =>
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(patientDeviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientDevice { Id = patientDeviceId, DeviceCategory = DeviceCategory.InsulinPump });

    [Fact]
    public async Task Create_PersistsExplicitPatientDeviceId()
    {
        var patientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(patientDeviceId);
        DeviceEvent? persisted = null;
        SetupCreatePassthrough(m => persisted = m);

        var result = await CreateController().Create(ValidRequest(patientDeviceId));

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

        var result = await CreateController().Create(ValidRequest(Guid.NewGuid()));

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(DeviceEventType.SensorChange, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.SensorStart, DeviceCategory.CGM)]
    [InlineData(DeviceEventType.SiteChange, DeviceCategory.InsulinPump)]
    [InlineData(DeviceEventType.ReservoirChange, DeviceCategory.InsulinPump)]
    public async Task Create_StampsUnattributedEvents_WithEventTypeCategory(DeviceEventType eventType, DeviceCategory expectedCategory)
    {
        SetupCreatePassthrough();

        await CreateController().Create(ValidRequest(eventType: eventType));

        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.Is<IReadOnlyList<DeviceCategory>>(c => c.Single() == expectedCategory),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_PersistsStampedAttribution()
    {
        var patientDeviceId = Guid.NewGuid();
        _deviceStamperMock
            .Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
                It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) => records[0].PatientDeviceId = patientDeviceId)
            .Returns(Task.CompletedTask);
        DeviceEvent? persisted = null;
        SetupCreatePassthrough(m => persisted = m);

        await CreateController().Create(ValidRequest());

        persisted.Should().NotBeNull();
        persisted!.PatientDeviceId.Should().Be(patientDeviceId);
    }

    [Fact]
    public async Task Update_PreservesAttribution_WhenRequestOmitsPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var existing = new DeviceEvent
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            EventType = DeviceEventType.SiteChange,
            DeviceId = Guid.NewGuid(),
            PatientDeviceId = Guid.NewGuid(),
        };
        _repoMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        DeviceEvent? updated = null;
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DeviceEvent, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, DeviceEvent m, WriteOrigin _, CancellationToken _) => m);

        var result = await CreateController().Update(id, ValidRequest());

        result.Result.Should().BeOfType<OkObjectResult>();
        updated.Should().NotBeNull();
        updated!.DeviceId.Should().Be(existing.DeviceId);
        updated.PatientDeviceId.Should().Be(existing.PatientDeviceId);
    }

    [Fact]
    public async Task Update_RelinksAttribution_WhenRequestCarriesPatientDeviceId()
    {
        var id = Guid.NewGuid();
        var newPatientDeviceId = Guid.NewGuid();
        SetupRegisteredDevice(newPatientDeviceId);
        var existing = new DeviceEvent
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            EventType = DeviceEventType.SiteChange,
            PatientDeviceId = Guid.NewGuid(),
        };
        _repoMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        DeviceEvent? updated = null;
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DeviceEvent, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, DeviceEvent m, WriteOrigin _, CancellationToken _) => m);

        await CreateController().Update(id, ValidRequest(newPatientDeviceId));

        updated.Should().NotBeNull();
        updated!.PatientDeviceId.Should().Be(newPatientDeviceId);
    }

    [Fact]
    public async Task Update_Returns400_WhenPatientDeviceIdDoesNotResolve()
    {
        var id = Guid.NewGuid();
        _repoMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceEvent { Id = id, Timestamp = DateTime.UtcNow, EventType = DeviceEventType.SiteChange });
        _patientDevicesMock
            .Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientDevice?)null);

        var result = await CreateController().Update(id, ValidRequest(Guid.NewGuid()));

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        DeviceEvent? persisted = null;
        SetupCreatePassthrough(m => persisted = m);

        var result = await CreateController().Create(ValidRequest(Guid.Empty));

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        persisted.Should().NotBeNull();
        persisted!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    [Fact]
    public async Task Update_ClearsAttribution_AndSkipsStamping_WhenRequestSendsTheClearSentinel()
    {
        var id = Guid.NewGuid();
        var existing = new DeviceEvent
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
            EventType = DeviceEventType.SiteChange,
            PatientDeviceId = Guid.NewGuid(),
        };
        _repoMock.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        DeviceEvent? updated = null;
        _repoMock
            .Setup(r => r.UpdateAsync(id, It.IsAny<DeviceEvent>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DeviceEvent, WriteOrigin, CancellationToken>((_, m, _, _) => updated = m)
            .ReturnsAsync((Guid _, DeviceEvent m, WriteOrigin _, CancellationToken _) => m);

        var result = await CreateController().Update(id, ValidRequest(Guid.Empty));

        result.Result.Should().BeOfType<OkObjectResult>();
        updated.Should().NotBeNull();
        updated!.PatientDeviceId.Should().BeNull();
        VerifyStamperNeverRan();
    }

    private void VerifyStamperNeverRan() =>
        _deviceStamperMock.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(),
            It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task GetAll_PassesPatientDeviceIdFilter_ToRepository()
    {
        var patientDeviceId = Guid.NewGuid();
        _repoMock
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["patientDeviceId"] = patientDeviceId.ToString(),
        });

        var result = await CreateController(query).GetAll(from: null, to: null);

        result.Result.Should().BeOfType<OkObjectResult>();
        _repoMock.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<bool>(), patientDeviceId,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
