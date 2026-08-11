using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.Controllers.V4;

[Trait("Category", "Unit")]
public class MeterGlucoseControllerTests
{
    private readonly Mock<IMeterGlucoseRepository> _repoMock = new();

    private MeterGlucoseController CreateController()
    {
        var controller = new MeterGlucoseController(_repoMock.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task Update_PreservesExistingPatientDeviceId()
    {
        var patientDeviceId = Guid.NewGuid();
        var id = Guid.NewGuid();
        MeterGlucose? captured = null;

        // IMeterGlucoseRepository shadows GetByIdAsync with `new`, and the base controller reaches the
        // repository through the IV4Repository<T> constraint, so the setup has to target that interface.
        var baseRepo = _repoMock.As<IV4Repository<MeterGlucose>>();
        baseRepo
            .Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MeterGlucose { Id = id, Timestamp = DateTime.UtcNow, Mgdl = 120, PatientDeviceId = patientDeviceId });
        baseRepo
            .Setup(r => r.UpdateAsync(id, It.IsAny<MeterGlucose>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, MeterGlucose, WriteOrigin, CancellationToken>((_, m, _, _) => captured = m)
            .ReturnsAsync((Guid _, MeterGlucose m, WriteOrigin _, CancellationToken _) => m);

        await CreateController().Update(id, new UpsertMeterGlucoseRequest { Timestamp = DateTimeOffset.UtcNow, Mgdl = 95 });

        captured.Should().NotBeNull();
        captured!.PatientDeviceId.Should().Be(patientDeviceId);
    }
}
