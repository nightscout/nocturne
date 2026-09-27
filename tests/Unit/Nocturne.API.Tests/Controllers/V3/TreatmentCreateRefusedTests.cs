using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 create the user's delete refused wrote nothing, so there is no created record to answer with.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentCreateRefusedTests
{
    private const string SyncIdentifier = "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f";

    private static TreatmentsController CreateController(BulkWrite<Treatment> written)
    {
        var documents = new Mock<IDocumentProcessingService>();
        documents.Setup(d => d.ProcessTreatment(It.IsAny<Treatment>())).Returns((Treatment t) => t);

        var treatments = new Mock<ITreatmentService>();
        treatments
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(written);

        return new TreatmentsController(
            Mock.Of<ITreatmentStore>(),
            documents.Object,
            treatments.Object,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task Create_RefusedByTheUsersDelete_AnswersAsADeduplication()
    {
        var controller = CreateController(new BulkWrite<Treatment>([], skippedDeleted: 1));

        var response = await controller.CreateTreatment(
            new Treatment { Id = SyncIdentifier, EventType = "Correction Bolus", Insulin = 0.65 });

        var ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(ok.Value);
        body.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        body.GetProperty("identifier").GetString().Should().Be(MongoObjectId.Coerce(SyncIdentifier));
        body.GetProperty("deduplicatedIdentifier").GetString().Should().Be(MongoObjectId.Coerce(SyncIdentifier));
    }

    [Fact]
    public async Task Create_ThatWroteNothingForAnotherReason_StillFails()
    {
        var controller = CreateController(new BulkWrite<Treatment>([], skippedDeleted: 0));

        var response = await controller.CreateTreatment(new Treatment { EventType = "Note", Notes = "x" });

        response.Result.Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
