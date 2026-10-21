using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// A v1 POST whose treatment fails to store answers as cgm-remote-monitor's ordered
/// <c>bulkWrite</c> does: the treatments before it stay stored, the ones after it are not
/// attempted, and the request answers 500 <c>{status, message: "Mongo Error", description}</c>.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsCreatePartialFailureTests
{
    private readonly Mock<ITreatmentDecomposer> _decomposer = new();
    private readonly Mock<ITreatmentCache> _cache = new();
    private readonly Mock<IDataEventSink<Treatment>> _events = new();
    private readonly TreatmentsController _controller;

    public TreatmentsCreatePartialFailureTests()
    {
        var store = new TreatmentReadService(
            Mock.Of<IV4ToLegacyProjectionService>(),
            _decomposer.Object,
            Mock.Of<IDecompositionPipeline>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        var service = new TreatmentService(
            store,
            _decomposer.Object,
            _cache.Object,
            _events.Object,
            Mock.Of<IPatientInsulinRepository>(),
            NullLogger<TreatmentService>.Instance);

        _controller = new TreatmentsController(
            service,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());
        _decomposer
            .Setup(d => d.DecomposeAsync(
                It.Is<Treatment>(t => t.Notes == "second"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("store down"));
    }

    [Fact]
    public async Task Post_WhenTheSecondOfThreeFailsToStore_Answers500AsNightscoutDoes()
    {
        var result = await _controller.CreateTreatments(Batch());

        var answer = result.Result.Should().BeOfType<ObjectResult>().Subject;
        answer.StatusCode.Should().Be(500);
        var body = JsonSerializer.SerializeToElement(answer.Value);
        body.GetProperty("status").GetInt32().Should().Be(500);
        body.GetProperty("message").GetString().Should().Be("Mongo Error");
        body.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Post_WhenATreatmentFailsToStore_DoesNotAttemptTheOnesAfterIt()
    {
        await _controller.CreateTreatments(Batch());

        _decomposer.Verify(
            d => d.DecomposeAsync(It.Is<Treatment>(t => t.Notes == "first"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _decomposer.Verify(
            d => d.DecomposeAsync(It.Is<Treatment>(t => t.Notes == "third"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Post_WhenATreatmentFailsToStore_AnnouncesTheOnesStoredBeforeIt()
    {
        await _controller.CreateTreatments(Batch());

        _cache.Verify(c => c.InvalidateAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _events.Verify(
            e => e.OnCreatedAsync(
                It.Is<IReadOnlyList<Treatment>>(written => written.Count == 1 && written[0].Notes == "first"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Post_WhenEveryTreatmentStores_EchoesEachInOrder()
    {
        _decomposer
            .Setup(d => d.DecomposeAsync(
                It.Is<Treatment>(t => t.Notes == "second"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var result = await _controller.CreateTreatments(Batch());

        var echoed = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<Treatment[]>().Subject;
        echoed.Select(t => t.Notes).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task Post_WhenATreatmentCanNeverBeStored_StoresTheRestAndEchoesItInPlaceWithAnError()
    {
        RefuseSecond();

        var result = await _controller.CreateTreatments(Batch());

        var echoed = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<Treatment[]>().Subject;
        echoed.Select(t => t.Notes).Should().Equal("first", "second", "third");
        echoed[1].AdditionalProperties.Should().ContainKey(TreatmentEcho.RefusalMember)
            .WhoseValue.Should().Be("A text value is longer than Nocturne can store; parts of the treatment may be stored");
        echoed[0].AdditionalProperties?.ContainsKey(TreatmentEcho.RefusalMember).Should().NotBe(true);
        _events.Verify(
            e => e.OnCreatedAsync(
                It.Is<IReadOnlyList<Treatment>>(written => written.Select(t => t.Notes).SequenceEqual(new[] { "first", "third" })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Put_WhenTheTreatmentCanNeverBeStored_Answers200WithTheRefusalOnItsEcho()
    {
        RefuseSecond();

        var result = await _controller.SaveTreatments(JsonSerializer.SerializeToElement(
            new { eventType = "Note", notes = "second", created_at = "2026-09-30T01:05:00.000Z" }));

        var echoed = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Treatment>().Subject;
        echoed.Notes.Should().Be("second");
        echoed.AdditionalProperties.Should().ContainKey(TreatmentEcho.RefusalMember);
    }

    private void RefuseSecond() =>
        _decomposer
            .Setup(d => d.DecomposeAsync(
                It.Is<Treatment>(t => t.Notes == "second"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException(
                "refused",
                new PostgresException("value too long for type character varying(255)", "ERROR", "ERROR", "22001")));

    private static JsonElement Batch() =>
        JsonSerializer.SerializeToElement(new[]
        {
            new { eventType = "Note", notes = "first", created_at = "2026-09-30T01:00:00.000Z" },
            new { eventType = "Note", notes = "second", created_at = "2026-09-30T01:05:00.000Z" },
            new { eventType = "Note", notes = "third", created_at = "2026-09-30T01:10:00.000Z" },
        });
}
