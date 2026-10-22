using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// A v3 create of one document answers what upstream answers, which is all AAPS reads back:
/// 201 <c>{status, identifier, lastModified}</c> with <c>Location</c> and <c>Last-Modified</c>.
/// </summary>
[Trait("Category", "Unit")]
public class V3CreateResponseTests
{
    private const long SrvModified = 1727654400000;
    private const string SubmittedId = "6a6a6a6a6a6a6a6a6a6a6a6a";
    private const string StoredId = "5f1a2b3c4d5e6f7a8b9c0d1e";

    [Fact]
    public async Task A_profile_create_answers_the_identifier_and_last_modified()
    {
        var writer = new Mock<IProfileWriteService>();
        writer
            .Setup(w => w.CreateProfilesAsync(It.IsAny<IEnumerable<Profile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Profile> p, CancellationToken _) =>
                p.Select(x => { x.Id = StoredId; return x; }).ToList());
        var controller = WithContext(new ProfileController(
            Mock.Of<IProfileProjectionService>(),
            writer.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance));

        var result = await controller.CreateProfile(Json("""{"defaultProfile":"Default","startDate":"2024-09-30T00:00:00.000Z"}"""));

        AssertCreated(result, controller, "GetProfileById");
    }

    [Fact]
    public async Task A_food_create_answers_the_identifier_and_last_modified()
    {
        var repository = new Mock<IFoodRepository>();
        repository
            .Setup(r => r.CreateFoodAsync(It.IsAny<IEnumerable<Food>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Food> f, CancellationToken _) =>
                f.Select(x => { x.Id = StoredId; return x; }).ToList());
        var controller = WithContext(new FoodController(
            repository.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<FoodController>.Instance));

        var result = await controller.CreateFood(Json("""{"name":"Apple","carbs":12,"date":1727654400000}"""));

        AssertCreated(result, controller, "GetFoodById");
    }

    [Fact]
    public async Task A_devicestatus_create_answers_the_identifier_and_last_modified()
    {
        var uploaders = new Mock<IUploaderSnapshotRepository>();
        var decomposer = new Mock<IDeviceStatusDecomposer>();
        decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());
        var controller = WithContext(new DeviceStatusController(
            new DeviceStatusProjectionService(
                Mock.Of<IDeviceStatusGroupRepository>(),
                Mock.Of<IApsSnapshotRepository>(),
                Mock.Of<IPumpSnapshotRepository>(),
                uploaders.Object,
                Mock.Of<IStateSpanRepository>(),
                Mock.Of<IDeviceStatusExtrasRepository>(),
                NullLogger<DeviceStatusProjectionService>.Instance),
            decomposer.Object,
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance));

        var result = await controller.CreateDeviceStatus(
            Json($$"""{"_id":"{{StoredId}}","device":"openaps://phone","app":"AAPS","mills":{{SrvModified}}}"""));

        AssertCreated(result, controller, "GetDeviceStatusById");
    }

    [Fact]
    public async Task An_entry_create_answers_the_identifier_and_last_modified()
    {
        var (controller, _) = EntriesController(existing: null);

        var result = await controller.CreateEntry(new Entry { Id = StoredId, Type = "sgv", Sgv = 120, Mills = SrvModified });

        AssertCreated(result, controller, "GetEntry");
    }

    [Fact]
    public async Task A_deduplicated_entry_answers_the_stored_identifier_not_the_submitted_one()
    {
        var stored = new Entry { Id = StoredId, Type = "sgv", Sgv = 120, Mills = SrvModified, SrvModified = SrvModified };
        var (controller, service) = EntriesController(existing: stored);

        var result = await controller.CreateEntry(new Entry { Id = SubmittedId, Type = "sgv", Sgv = 120, Mills = SrvModified });

        AssertDeduplicated(result, controller);
        service.Verify(
            s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_treatment_create_answers_the_identifier_and_last_modified()
    {
        var (controller, _) = TreatmentsController(existing: null);

        var result = await controller.CreateTreatment(new Treatment { Id = SubmittedId, EventType = "Note" });

        AssertCreated(result, controller, "GetTreatment");
    }

    [Fact]
    public async Task A_deduplicated_treatment_answers_the_stored_identifier()
    {
        var stored = new Treatment { Id = StoredId, EventType = "Note", SrvModified = SrvModified };
        var (controller, service) = TreatmentsController(existing: stored);

        var result = await controller.CreateTreatment(new Treatment { Id = SubmittedId, EventType = "Note" });

        AssertDeduplicated(result, controller);
        service.Verify(
            s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_profile_array_create_still_answers_the_created_documents()
    {
        var writer = new Mock<IProfileWriteService>();
        writer
            .Setup(w => w.CreateProfilesAsync(It.IsAny<IEnumerable<Profile>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Profile> p, CancellationToken _) => p.ToList());
        var controller = WithContext(new ProfileController(
            Mock.Of<IProfileProjectionService>(),
            writer.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<ProfileController>.Instance));

        var result = await controller.CreateProfile(Json("""[{"defaultProfile":"A"},{"defaultProfile":"B"}]"""));

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(201);
        status.Value.Should().BeOfType<Profile[]>().Which.Should().HaveCount(2);
    }

    private static (EntriesController, Mock<IEntryService>) EntriesController(Entry? existing)
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessEntry(It.IsAny<Entry>())).Returns<Entry>(e => e);
        var service = new Mock<IEntryService>();
        service
            .Setup(s => s.CheckForDuplicateEntryAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        service
            .Setup(s => s.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Entry> e, WriteOrigin _, CancellationToken _) => new BulkWrite<Entry>(e.ToList(), 0));
        var controller = WithContext(new EntriesController(
            processing.Object,
            service.Object,
            Mock.Of<ICanonicalAlertEvaluator>(),
            NullLogger<EntriesController>.Instance));
        return (controller, service);
    }

    private static (TreatmentsController, Mock<ITreatmentService>) TreatmentsController(Treatment? existing)
    {
        var processing = new Mock<IDocumentProcessingService>();
        processing.Setup(p => p.ProcessTreatment(It.IsAny<Treatment>())).Returns<Treatment>(t => t);
        var service = new Mock<ITreatmentService>();
        service
            .Setup(s => s.GetTreatmentByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        service
            .Setup(s => s.CreateTreatmentsAsync(It.IsAny<IEnumerable<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Treatment> t, CancellationToken _) =>
                new BulkWrite<Treatment>(t.Select(x => { x.Id = StoredId; return x; }).ToList(), 0));
        var controller = WithContext(new TreatmentsController(
            processing.Object,
            service.Object,
            NullLogger<TreatmentsController>.Instance));
        return (controller, service);
    }

    private static void AssertCreated(IActionResult result, ControllerBase controller, string getAction)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        created.ActionName.Should().Be(getAction);
        created.RouteValues!["id"].Should().Be(StoredId);
        var body = JsonSerializer.SerializeToElement(created.Value);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "identifier", "lastModified");
        body.GetProperty("status").GetInt32().Should().Be(201);
        body.GetProperty("identifier").GetString().Should().Be(StoredId);
        var lastModified = body.GetProperty("lastModified").GetInt64();
        lastModified.Should().BeInRange(before - 60_000, before + 1_000);
        controller.Response.Headers["Last-Modified"].ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(lastModified).UtcDateTime.ToString("R"));
    }

    private static void AssertDeduplicated(IActionResult result, ControllerBase controller)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = JsonSerializer.SerializeToElement(ok.Value);
        body.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("status", "identifier", "isDeduplication", "lastModified");
        body.GetProperty("status").GetInt32().Should().Be(200);
        body.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        body.GetProperty("identifier").GetString().Should().Be(StoredId);
        body.GetProperty("lastModified").GetInt64().Should().Be(SrvModified);
        controller.Response.Headers["Last-Modified"].ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(SrvModified).UtcDateTime.ToString("R"));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
