using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Services.Legacy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Nightscout's PUT /api/v1/treatments is an upsert keyed on the body's <c>_id</c>: Loop edits a
/// carb entry or replaces an override that way, and an unknown <c>_id</c> inserts.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentsSaveTests
{
    private const string LoopId = "69F15FD2-8075-4DEB-AEA3-4352F455840D";

    private readonly Mock<ITreatmentService> _service = new();
    private readonly TreatmentsController _controller;

    public TreatmentsSaveTests()
    {
        _controller = new TreatmentsController(
            _service.Object,
            new DocumentProcessingService(NullLogger<DocumentProcessingService>.Instance),
            TimeProvider.System,
            NullLogger<TreatmentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> t, CancellationToken _) =>
                new BulkWrite<Treatment>(t.ToList(), 0));
    }

    [Fact]
    public async Task Put_WithAStoredId_UpdatesThatRecord()
    {
        var stored = new Treatment { Id = LoopId, EventType = "Temporary Override", Duration = 30 };
        _service
            .Setup(s => s.UpdateTreatmentAsync(LoopId, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(stored);
        _service.Verify(s => s.UpdateTreatmentAsync(
            LoopId, It.Is<Treatment>(t => t.Duration == 30), It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WithAnUnknownId_InsertsUnderTheClientsId()
    {
        UnknownIds();

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        var saved = Saved(result);
        saved.Id.Should().Be(LoopId);
        saved.Duration.Should().Be(30);
        _service.Verify(s => s.CreateTreatmentsAsync(
            It.Is<IEnumerable<Treatment>>(t => t.Single().Id == LoopId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"_id\":\"507f1f77bcf86cd799439011\",\"eventType\":\"Note\"}]")]
    [InlineData("\"note\"")]
    public async Task Put_WithAnythingButAnObject_IsRefused(string json)
    {
        var result = await _controller.SaveTreatments(JsonDocument.Parse(json).RootElement);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Put_WithAnIdentifier_MatchesOnItBeforeTheId()
    {
        const string identifier = "0E5B7B1C-6E5A-4E9B-9C55-0B8C0C3E2A11";
        UnknownIds();

        var result = await _controller.SaveTreatments(Body(new
        {
            identifier,
            _id = "507f1f77bcf86cd799439011",
            eventType = "Note",
            created_at = "2026-09-30T01:00:00.000Z",
        }));

        Saved(result).Id.Should().Be(identifier);
        _service.Verify(s => s.UpdateTreatmentAsync(
            identifier, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Put_WithABlankIdentifier_FallsBackToTheId()
    {
        const string storedId = "507f1f77bcf86cd799439011";
        UnknownIds();

        var result = await _controller.SaveTreatments(Body(new
        {
            identifier = " ",
            _id = storedId,
            eventType = "Note",
            created_at = "2026-09-30T01:00:00.000Z",
        }));

        Saved(result).Id.Should().Be(storedId);
    }

    [Fact]
    public async Task Put_KeepsTheClientsCreatedAt()
    {
        UnknownIds();

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        var saved = Saved(result);
        saved.CreatedAt.Should().Be("2026-09-30T01:45:00.000Z");
        saved.Mills.Should().Be(DateTimeOffset.Parse("2026-09-30T01:45:00Z").ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(1.5, 20.0, "Meal Bolus")]
    [InlineData(1.5, null, "Correction Bolus")]
    [InlineData(null, 20.0, "Carb Correction")]
    [InlineData(null, null, "Note")]
    public async Task Put_WithoutAnEventType_DefaultsItAsPostDoes(
        double? insulin, double? carbs, string expected)
    {
        var result = await _controller.SaveTreatments(
            Body(new { insulin, carbs, created_at = "2026-09-30T01:00:00.000Z" }));

        Saved(result).EventType.Should().Be(expected);
    }

    [Fact]
    public async Task Put_WhenTheStoreKeepsNothing_Answers500()
    {
        _service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkWrite<Treatment>([], 0));

        var result = await _controller.SaveTreatments(
            Body(new { eventType = "Note", created_at = "2026-09-30T01:00:00.000Z" }));

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Put_WhenTheServiceThrows_LeavesItToTheExceptionHandler()
    {
        _service
            .Setup(s => s.UpdateTreatmentAsync(It.IsAny<string>(), It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var save = () => _controller.SaveTreatments(Body(Override(duration: 30)));

        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Put_WithAnIdThatResolvesToNothing_UpdatesTheTreatmentItsSyncIdentifierNames()
    {
        const string echoedId = "3f0b8b54aa1c4f7d8e2a6c11";
        const string syncIdentifier = "3F0B8B54-AA1C-4F7D-8E2A-6C11D0E5F9A2";
        var stored = new Treatment { Id = Guid.CreateVersion7().ToString(), EventType = "Carb Correction", Carbs = 35 };
        UnknownIds();
        _service
            .Setup(s => s.UpdateTreatmentAsync(syncIdentifier, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await _controller.SaveTreatments(Body(new
        {
            _id = echoedId,
            syncIdentifier,
            eventType = "Carb Correction",
            carbs = 35,
            created_at = "2026-09-30T01:00:00.000Z",
        }));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(stored);
        _service.Verify(s => s.UpdateTreatmentAsync(
            echoedId, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WithoutAnId_ReplacesTheTreatmentStoredAtTheSameTimeWithTheSameEventType()
    {
        var mills = DateTimeOffset.Parse("2026-09-30T01:00:00Z").ToUnixTimeMilliseconds();
        var sameEvent = Guid.CreateVersion7().ToString();
        var replaced = new Treatment { Id = sameEvent, EventType = "Note", Notes = "edited" };
        _service
            .Setup(s => s.GetTreatmentsByRangeAsync(mills, mills, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Treatment { Id = Guid.CreateVersion7().ToString(), EventType = "Correction Bolus", Mills = mills },
                new Treatment { Id = sameEvent, EventType = "Note", Mills = mills },
            ]);
        _service
            .Setup(s => s.UpdateTreatmentAsync(sameEvent, It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(replaced);

        var result = await _controller.SaveTreatments(
            Body(new { eventType = "Note", notes = "edited", created_at = "2026-09-30T01:00:00.000Z" }));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(replaced);
        _service.Verify(s => s.UpdateTreatmentAsync(
            sameEvent, It.Is<Treatment>(t => t.Notes == "edited"), It.IsAny<CancellationToken>()), Times.Once);
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WithoutAnId_CreatesWhenNothingIsStoredAtThatTimeWithThatEventType()
    {
        var mills = DateTimeOffset.Parse("2026-09-30T01:00:00Z").ToUnixTimeMilliseconds();
        _service
            .Setup(s => s.GetTreatmentsByRangeAsync(mills, mills, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = Guid.CreateVersion7().ToString(), EventType = "Correction Bolus", Mills = mills }]);

        var result = await _controller.SaveTreatments(
            Body(new { eventType = "Note", notes = "hello", created_at = "2026-09-30T01:00:00.000Z" }));

        Saved(result).Notes.Should().Be("hello");
        _service.Verify(s => s.UpdateTreatmentAsync(
            It.IsAny<string>(), It.IsAny<Treatment>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(s => s.CreateTreatmentsAsync(
            It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(LoopId)]
    [InlineData("3f0b8b54aa1c4f7d8e2a6c11")]
    public async Task Put_WithTheIdOfATreatmentTheUserDeleted_SavesNothing(string id)
    {
        UnknownIds();
        _service
            .Setup(s => s.IsTreatmentDeletedByUserAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.SaveTreatments(Body(new
        {
            _id = id,
            eventType = "Carb Correction",
            carbs = 20,
            created_at = "2026-09-30T01:00:00.000Z",
        }));

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Treatment[]>().Which.Should().BeEmpty();
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WhoseSyncIdentifierNamesATreatmentTheUserDeleted_SavesNothing()
    {
        const string syncIdentifier = "3F0B8B54-AA1C-4F7D-8E2A-6C11D0E5F9A2";
        UnknownIds();
        _service
            .Setup(s => s.IsTreatmentDeletedByUserAsync(syncIdentifier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.SaveTreatments(Body(new
        {
            _id = "3f0b8b54aa1c4f7d8e2a6c11",
            syncIdentifier,
            eventType = "Carb Correction",
            carbs = 20,
            created_at = "2026-09-30T01:00:00.000Z",
        }));

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Treatment[]>().Which.Should().BeEmpty();
        VerifyNothingCreated();
    }

    [Fact]
    public async Task Put_WhenTheCreateSkipsATreatmentTheUserDeleted_SavesNothing()
    {
        UnknownIds();
        _service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> t, CancellationToken _) => new BulkWrite<Treatment>(t.ToList(), 1));

        var result = await _controller.SaveTreatments(Body(Override(duration: 30)));

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<Treatment[]>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task PutById_WithAnUnknownId_InsertsUnderThatId()
    {
        UnknownIds();

        var result = await _controller.UpdateTreatment(
            LoopId, new Treatment { EventType = "Temporary Override", Duration = 30 });

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Treatment>().Which.Id.Should().Be(LoopId);
    }

    [Fact]
    public async Task PutById_WithoutABody_IsRefused()
    {
        var result = await _controller.UpdateTreatment(LoopId, null!);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _service.VerifyNoOtherCalls();
    }

    private static Treatment Saved(ActionResult<Treatment> result) =>
        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Treatment>().Subject;

    private void UnknownIds() => _service
        .Setup(s => s.UpdateTreatmentAsync(It.IsAny<string>(), It.IsAny<Treatment>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((Treatment?)null);

    private void VerifyNothingCreated() => _service.Verify(s => s.CreateTreatmentsAsync(
        It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()), Times.Never);

    private static object Override(double duration) => new
    {
        _id = LoopId,
        eventType = "Temporary Override",
        created_at = "2026-09-30T01:45:00.000Z",
        duration,
    };

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);
}
