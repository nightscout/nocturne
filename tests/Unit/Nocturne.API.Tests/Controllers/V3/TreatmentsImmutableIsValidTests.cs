using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// Nightscout's v3 PUT and PATCH refuse <c>isValid: false</c> as an immutable field: a treatment is
/// deleted by DELETE.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsImmutableIsValidTests
{
    private readonly Mock<ITreatmentService> _treatments = new();

    private TreatmentsController CreateController()
    {
        var documents = new Mock<IDocumentProcessingService>();
        documents.Setup(d => d.ProcessTreatment(It.IsAny<Treatment>())).Returns((Treatment t) => t);
        return new TreatmentsController(documents.Object, _treatments.Object, NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static void ShouldBeImmutableFieldRefusal(IActionResult result)
    {
        var refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(refused.Value)
            .Should().Be("{\"status\":400,\"message\":\"Field isValid cannot be modified by the client\"}");
    }

    [Fact]
    public async Task A_put_setting_isValid_false_is_refused_and_writes_nothing()
    {
        var result = await CreateController().UpdateTreatment(
            "abc", new Treatment { EventType = "Correction Bolus", Insulin = 1, IsValid = false });

        ShouldBeImmutableFieldRefusal(result.Result!);
        _treatments.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_patch_setting_isValid_false_is_refused_and_writes_nothing()
    {
        using var patch = JsonDocument.Parse("""{"isValid":false}""");

        var result = await CreateController().PatchTreatment("abc", patch.RootElement);

        ShouldBeImmutableFieldRefusal(result);
        _treatments.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("""{"duration":5}""")]
    [InlineData("""{"isValid":true}""")]
    public async Task A_patch_leaving_isValid_unset_or_true_is_applied(string body)
    {
        using var patch = JsonDocument.Parse(body);
        _treatments
            .Setup(s => s.PatchTreatmentAsync("abc", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = "abc" });

        var result = await CreateController().PatchTreatment("abc", patch.RootElement);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_put_leaving_isValid_unset_is_applied()
    {
        _treatments
            .Setup(s => s.UpdateTreatmentAsync("abc", It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Treatment { Id = "abc" });

        var result = await CreateController().UpdateTreatment(
            "abc", new Treatment { EventType = "Correction Bolus", Insulin = 1 });

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
