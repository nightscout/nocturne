using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
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
    private readonly List<DeviceStatus> _created = [];
    private readonly Mock<IDeviceStatusService> _deviceStatuses = new();

    public DeviceStatusCreateIdentityTests()
    {
        _deviceStatuses
            .Setup(s => s.CreateDeviceStatusesAsync(
                It.IsAny<IReadOnlyList<DeviceStatus>>(), It.IsAny<string?>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<DeviceStatus>, string?, WriteOrigin, CancellationToken>((statuses, _, _, _) => _created.AddRange(statuses))
            .ReturnsAsync((IReadOnlyList<DeviceStatus> statuses, string? _, WriteOrigin _, CancellationToken _) =>
                new BulkWrite<DeviceStatus>(statuses, 0));
    }

    private V1DeviceStatusController V1() => WithContext(new V1DeviceStatusController(
        _deviceStatuses.Object,
        TimeProvider.System,
        NullLogger<V1DeviceStatusController>.Instance));

    private V3DeviceStatusController V3() => WithContext(new V3DeviceStatusController(
        _deviceStatuses.Object,
        Mock.Of<IDocumentProcessingService>(),
        NullLogger<V3DeviceStatusController>.Instance));

    [Fact]
    public async Task V1_create_without_an_id_stores_each_status_under_its_own_object_id()
    {
        var result = await V1().CreateDeviceStatus(Json("""[{"device":"loop://a"},{"device":"loop://b","_id":""}]"""));

        _created.Should().HaveCount(2);
        _created.Should().OnlyContain(ds => MongoObjectId.IsObjectId(ds.Id));
        _created[0].Id.Should().NotBe(_created[1].Id);
        var returned = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<DeviceStatus[]>().Subject;
        returned.Select(ds => ds.Id).Should().Equal(_created.Select(ds => ds.Id));
    }

    [Fact]
    public async Task V1_create_keeps_an_uploaded_id()
    {
        await V1().CreateDeviceStatus(Json("""{"device":"loop://a","_id":"loop-status-7"}"""));

        _created.Should().ContainSingle().Which.Id.Should().Be("loop-status-7");
    }

    [Fact]
    public async Task V3_create_without_an_identifier_stores_the_status_under_an_object_id()
    {
        await V3().CreateDeviceStatus(Json("""{"device":"openaps://phone","app":"AAPS"}"""));

        MongoObjectId.IsObjectId(_created.Should().ContainSingle().Subject.Id).Should().BeTrue();
    }

    /// <summary>
    /// A re-sent status is answered with the identifier its first create returned, which is the
    /// wire form of its stored id, not the raw id.
    /// </summary>
    [Fact]
    public async Task V3_deduplicated_create_answers_with_the_wire_identifier()
    {
        _deviceStatuses
            .Setup(s => s.GetDeviceStatusByIdAsync("loop-status-7", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceStatus
            {
                Id = "loop-status-7", Device = "loop://a", SrvModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

        var result = await V3().CreateDeviceStatus(Json("""{"_id":"loop-status-7","device":"loop://a","app":"Loop"}"""));

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Which.Value);
        var wireId = MongoObjectId.Coerce("loop-status-7");
        body.GetProperty("isDeduplication").GetBoolean().Should().BeTrue();
        body.GetProperty("identifier").GetString().Should().Be(wireId);
        body.GetProperty("status").GetInt32().Should().Be(200);
        body.TryGetProperty("lastModified", out _).Should().BeTrue();
        _created.Should().BeEmpty();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static T WithContext<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
