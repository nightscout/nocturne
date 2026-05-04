using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class BolusControllerTests
{
    private readonly Mock<IBolusRepository> _repoMock = new();
    private readonly Mock<IPatientInsulinRepository> _insulinRepoMock = new();

    private BolusController CreateController()
    {
        var controller = new BolusController(_repoMock.Object, _insulinRepoMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private void SetupCreatePassthrough(Action<Bolus> onCreate)
    {
        _repoMock
            .Setup(r => r.CreateAsync(It.IsAny<Bolus>(), It.IsAny<CancellationToken>()))
            .Callback<Bolus, CancellationToken>((b, _) => onCreate(b))
            .ReturnsAsync((Bolus b, CancellationToken _) => b);
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
            .Setup(r => r.UpdateAsync(id, It.IsAny<Bolus>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Bolus, CancellationToken>((_, b, _) => captured = b)
            .ReturnsAsync((Guid _, Bolus b, CancellationToken _) => b);

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
            .Setup(r => r.UpdateAsync(id, It.IsAny<Bolus>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Bolus, CancellationToken>((_, b, _) => captured = b)
            .ReturnsAsync((Guid _, Bolus b, CancellationToken _) => b);

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
