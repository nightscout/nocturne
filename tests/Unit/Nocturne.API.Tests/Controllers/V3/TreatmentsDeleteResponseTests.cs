using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

[Trait("Category", "Unit")]
public class TreatmentsDeleteResponseTests
{
    private static TreatmentsController CreateController(Mock<ITreatmentService> treatmentService)
    {
        return new TreatmentsController(
            Mock.Of<IDocumentProcessingService>(),
            treatmentService.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task DeleteTreatment_Existing_AnswersStatusEnvelopeWithoutResult()
    {
        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.DeleteTreatmentAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await CreateController(treatmentService).DeleteTreatment("abc");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        JsonSerializer.Serialize(ok.Value).Should().Be("{\"status\":200}");
    }

    [Fact]
    public async Task DeleteTreatment_Missing_StillAnswers404()
    {
        var treatmentService = new Mock<ITreatmentService>();
        treatmentService
            .Setup(s => s.DeleteTreatmentAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateController(treatmentService).DeleteTreatment("abc");

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }
}
