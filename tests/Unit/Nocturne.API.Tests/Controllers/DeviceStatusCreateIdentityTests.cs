using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;
using V1DeviceStatusController = Nocturne.API.Controllers.V1.DeviceStatusController;
using V3DeviceStatusController = Nocturne.API.Controllers.V3.DeviceStatusController;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// A devicestatus uploaded without an id is stored under a fresh ObjectId. That id is its legacy
/// key, so the create response, the socket event and every later lookup carry it verbatim; a
/// record stored with no key, or with one the wire hashes, has an id no client can resolve.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusCreateIdentityTests
{
    private readonly List<DeviceStatus> _decomposed = [];
    private readonly Mock<IUploaderSnapshotRepository> _uploaderRepo = new();
    private readonly Mock<IDeviceStatusDecomposer> _decomposer = new();

    public DeviceStatusCreateIdentityTests()
    {
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<DeviceStatus>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<DeviceStatus, string?, WriteOrigin, CancellationToken>((ds, _, _, _) => _decomposed.Add(ds))
            .ReturnsAsync(new DecompositionResult());
    }

    private DeviceStatusProjectionService Projection() => new(
        Mock.Of<IApsSnapshotRepository>(),
        Mock.Of<IPumpSnapshotRepository>(),
        _uploaderRepo.Object,
        Mock.Of<IStateSpanRepository>(),
        Mock.Of<IDeviceStatusExtrasRepository>(),
        NullLogger<DeviceStatusProjectionService>.Instance);

    private V1DeviceStatusController V1() => WithContext(new V1DeviceStatusController(
        Projection(),
        _decomposer.Object,
        Mock.Of<IWriteSideEffects>(),
        Mock.Of<IDataEventSink<DeviceStatus>>(),
        NullLogger<V1DeviceStatusController>.Instance));

    private V3DeviceStatusController V3() => WithContext(new V3DeviceStatusController(
        Projection(),
        _decomposer.Object,
        Mock.Of<IWriteSideEffects>(),
        Mock.Of<IDataEventSink<DeviceStatus>>(),
        Mock.Of<IDocumentProcessingService>(),
        NullLogger<V3DeviceStatusController>.Instance));

    [Fact]
    public async Task V1_create_without_an_id_stores_each_status_under_its_own_object_id()
    {
        var result = await V1().CreateDeviceStatus(Json("""[{"device":"loop://a"},{"device":"loop://b","_id":""}]"""));

        _decomposed.Should().HaveCount(2);
        _decomposed.Should().OnlyContain(ds => MongoObjectId.IsObjectId(ds.Id));
        _decomposed[0].Id.Should().NotBe(_decomposed[1].Id);
        var returned = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<DeviceStatus[]>().Subject;
        returned.Select(ds => ds.Id).Should().Equal(_decomposed.Select(ds => ds.Id));
    }

    [Fact]
    public async Task V1_create_keeps_an_uploaded_id()
    {
        await V1().CreateDeviceStatus(Json("""{"device":"loop://a","_id":"loop-status-7"}"""));

        _decomposed.Should().ContainSingle().Which.Id.Should().Be("loop-status-7");
    }

    [Fact]
    public async Task V3_create_without_an_identifier_stores_the_status_under_an_object_id()
    {
        await V3().CreateDeviceStatus(Json("""{"device":"openaps://phone","app":"AAPS"}"""));

        MongoObjectId.IsObjectId(_decomposed.Should().ContainSingle().Subject.Id).Should().BeTrue();
    }

    /// <summary>
    /// A re-sent status is answered with the identifier its first create returned, which is the
    /// wire form of its stored id, not the raw id.
    /// </summary>
    [Fact]
    public async Task V3_deduplicated_create_answers_with_the_wire_identifier()
    {
        _uploaderRepo
            .Setup(r => r.GetByLegacyIdAsync("loop-status-7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UploaderSnapshot { Id = Guid.CreateVersion7(), LegacyId = "loop-status-7", Timestamp = DateTime.UtcNow });

        var result = await V3().CreateDeviceStatus(Json("""{"_id":"loop-status-7","device":"loop://a","app":"Loop"}"""));

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Which.Value);
        var wireId = MongoObjectId.Coerce("loop-status-7");
        body.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        body.GetProperty("identifier").GetString().Should().Be(wireId);
        body.GetProperty("deduplicatedIdentifier").GetString().Should().Be(wireId);
        _decomposed.Should().BeEmpty();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
