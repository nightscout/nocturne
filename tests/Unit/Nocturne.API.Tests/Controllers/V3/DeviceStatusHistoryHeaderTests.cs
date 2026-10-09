using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// The history cursor headers must carry the same clock as the body's <c>srvModified</c>, which the
/// next request pages on. A status written well after its event makes the two clocks differ.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusHistoryHeaderTests
{
    [Fact]
    public async Task GetDeviceStatusHistory_SetsCursorHeadersFromServerWriteTime()
    {
        var eventTime = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var writtenAt = eventTime.AddHours(3);
        var aps = new ApsSnapshot
        {
            Id = Guid.NewGuid(),
            Timestamp = eventTime,
            ModifiedAt = writtenAt,
            AidAlgorithm = AidAlgorithm.Loop,
        };

        var controller = Controller(new HistoryRecord<ApsSnapshot>(aps, Deleted: false));

        await controller.GetDeviceStatusHistory(0);

        var written = new DateTimeOffset(writtenAt).ToUnixTimeMilliseconds();
        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{written}\"");
        controller.Response.Headers["Last-Modified"].ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(written).UtcDateTime.ToString("R"));
    }

    [Fact]
    public async Task GetDeviceStatusHistory_DeletedStatus_IsServedWithIsValidFalse()
    {
        var aps = new ApsSnapshot
        {
            Id = Guid.NewGuid(),
            Timestamp = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            ModifiedAt = new DateTime(2024, 3, 26, 13, 0, 0, DateTimeKind.Utc),
            AidAlgorithm = AidAlgorithm.Loop,
        };

        var result = await Controller(new HistoryRecord<ApsSnapshot>(aps, Deleted: true))
            .GetDeviceStatusHistory(0);

        var body = JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        var status = body.GetProperty("result").EnumerateArray().Should().ContainSingle().Subject;
        status.GetProperty("isValid").GetBoolean().Should().BeFalse();
        status.GetProperty("srvModified").GetInt64()
            .Should().Be(new DateTimeOffset(aps.ModifiedAt).ToUnixTimeMilliseconds());
    }

    private static DeviceStatusController Controller(params HistoryRecord<ApsSnapshot>[] page)
    {
        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var projection = new DeviceStatusProjectionService(
            apsRepo.Object,
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance);

        return new DeviceStatusController(
            projection,
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
