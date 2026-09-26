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

        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { aps });
        var stateSpans = new Mock<IStateSpanRepository>();
        var projection = new DeviceStatusProjectionService(
            apsRepo.Object,
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            stateSpans.Object,
            Mock.Of<IDeviceStatusExtrasRepository>(),
            NullLogger<DeviceStatusProjectionService>.Instance);

        var controller = new DeviceStatusController(
            projection,
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetDeviceStatusHistory(0);

        var written = new DateTimeOffset(writtenAt).ToUnixTimeMilliseconds();
        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{written}\"");
        controller.Response.Headers["Last-Modified"].ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(written).UtcDateTime.ToString("R"));
    }
}
