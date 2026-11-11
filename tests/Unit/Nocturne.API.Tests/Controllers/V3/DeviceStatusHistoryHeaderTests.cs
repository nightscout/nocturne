using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Models;
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
        var eventTime = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero);
        var written = eventTime.AddHours(3).ToUnixTimeMilliseconds();

        var controller = Controller(new DeviceStatus
        {
            Id = "5f8d0c1e8a7b4c3d9e5f0001", Mills = eventTime.ToUnixTimeMilliseconds(), SrvModified = written,
        });

        await controller.GetDeviceStatusHistory(0);

        controller.Response.Headers["ETag"].ToString().Should().Be($"W/\"{written}\"");
        controller.Response.Headers["Last-Modified"].ToString()
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(written).UtcDateTime.ToString("R"));
    }

    [Fact]
    public async Task GetDeviceStatusHistory_DeletedStatus_IsServedWithIsValidFalse()
    {
        var modified = new DateTimeOffset(2024, 3, 26, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = await Controller(new DeviceStatus
            {
                Id = "5f8d0c1e8a7b4c3d9e5f0001",
                Mills = new DateTimeOffset(2024, 3, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                SrvModified = modified,
                IsValid = false,
            })
            .GetDeviceStatusHistory(0);

        var body = JsonSerializer.SerializeToElement(
            result.Should().BeOfType<OkObjectResult>().Subject.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var status = body.GetProperty("result").EnumerateArray().Should().ContainSingle().Subject;
        status.GetProperty("isValid").GetBoolean().Should().BeFalse();
        status.GetProperty("srvModified").GetInt64().Should().Be(modified);
    }

    private static DeviceStatusController Controller(params DeviceStatus[] page)
    {
        var deviceStatuses = new Mock<IDeviceStatusService>();
        deviceStatuses
            .Setup(s => s.GetDeviceStatusesModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        return new DeviceStatusController(
            deviceStatuses.Object,
            Mock.Of<IDocumentProcessingService>(),
            NullLogger<DeviceStatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
