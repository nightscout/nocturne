using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Treatments;

[Trait("Category", "Unit")]
public class BasalInjectionControllerTests
{
    private readonly Mock<IBasalInjectionRepository> _repoMock = new();
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock = new();

    private BasalInjectionController CreateController()
    {
        var controller = new BasalInjectionController(_repoMock.Object, _insulinRepoMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static PatientInsulin BasalInsulin(
        Guid? id = null,
        InsulinRole role = InsulinRole.Basal,
        DateOnly? startDate = null,
        DateOnly? endDate = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Tresiba",
            Dia = 24,
            Peak = 720,
            Curve = "ultra-long",
            Concentration = 100,
            Role = role,
            StartDate = startDate,
            EndDate = endDate,
        };

    private void SetupCreatePassthrough(Action<BasalInjection> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()))
            .Callback<BasalInjection, CancellationToken>((b, _) => onCreate(b))
            .ReturnsAsync((BasalInjection b, CancellationToken _) => b);
    }

    [Fact]
    public async Task Create_returns_400_when_units_is_zero_or_negative()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 0,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);

        var negativeRequest = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = -1.5,
        };

        var negativeResult = await controller.Create(negativeRequest);
        negativeResult.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Create_returns_400_when_units_exceeds_500()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 500.01,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_timestamp_is_more_than_5_minutes_in_future()
    {
        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(10),
            PatientInsulinId = Guid.NewGuid(),
            Units = 12,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_not_found()
    {
        var insulinId = Guid.NewGuid();
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientInsulin?)null);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_role_is_BolusOnly()
    {
        var insulinId = Guid.NewGuid();
        var insulin = BasalInsulin(insulinId, role: InsulinRole.Bolus);
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_400_when_PatientInsulin_inactive_at_timestamp()
    {
        var insulinId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // EndDate before injection time -> inactive
        var insulin = BasalInsulin(
            insulinId,
            role: InsulinRole.Basal,
            startDate: today.AddDays(-30),
            endDate: today.AddDays(-1));
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 10,
        };

        var result = await controller.Create(request);

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(400);
        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_returns_existing_row_when_DataSource_and_SyncIdentifier_match_existing()
    {
        var existing = new BasalInjection
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow.AddHours(-1),
            Units = 14,
            DataSource = "loop",
            SyncIdentifier = "abc-123",
        };

        _repoMock.Setup(r => r.FindBySyncIdentifierAsync("loop", "abc-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = Guid.NewGuid(),
            Units = 14,
            DataSource = "loop",
            SyncIdentifier = "abc-123",
        };

        var result = await controller.Create(request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(existing);

        _repoMock.Verify(r => r.CreateAsync(It.IsAny<BasalInjection>(), It.IsAny<CancellationToken>()), Times.Never);
        _insulinRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_creates_row_and_enriches_InsulinContext()
    {
        var insulinId = Guid.NewGuid();
        var insulin = new PatientInsulin
        {
            Id = insulinId,
            Name = "Tresiba",
            Dia = 24,
            Peak = 720,
            Curve = "ultra-long",
            Concentration = 100,
            Role = InsulinRole.Basal,
        };
        _insulinRepoMock.Setup(r => r.GetByIdAsync(insulinId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(insulin);

        BasalInjection? captured = null;
        SetupCreatePassthrough(b => captured = b);

        var controller = CreateController();
        var request = new CreateBasalInjectionRequest
        {
            Timestamp = DateTimeOffset.UtcNow,
            PatientInsulinId = insulinId,
            Units = 18,
        };

        var result = await controller.Create(request);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured.Should().NotBeNull();
        captured!.Units.Should().Be(18);
        captured.InsulinContext.Should().NotBeNull();
        captured.InsulinContext!.PatientInsulinId.Should().Be(insulinId);
        captured.InsulinContext.InsulinName.Should().Be("Tresiba");
        captured.InsulinContext.Dia.Should().Be(24);
        captured.InsulinContext.Peak.Should().Be(720);
        captured.InsulinContext.Curve.Should().Be("ultra-long");
        captured.InsulinContext.Concentration.Should().Be(100);
        captured.CorrelationId.Should().NotBeNull().And.NotBe(Guid.Empty);
    }
}
