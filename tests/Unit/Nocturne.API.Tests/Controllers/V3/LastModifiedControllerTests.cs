using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// The <c>/api/v3/lastModified</c> response must use the Nightscout V3 envelope AAPS
/// expects: <c>{ status, result: { srvDate, collections } }</c> with Unix-millisecond
/// timestamps and lowercase collection keys. A raw serialization of the C# model
/// (ISO strings, camelCase <c>deviceStatus</c>/<c>serverTime</c>) makes AAPS report a
/// failed sync.
/// </summary>
[Trait("Category", "Unit")]
public class LastModifiedControllerTests
{
    private static LastModifiedController CreateController(Mock<IStatusService> statusService)
    {
        return new LastModifiedController(statusService.Object, NullLogger<LastModifiedController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    private static JsonElement CaptureBody(IActionResult result)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Fact]
    public async Task GetLastModified_WrapsInV3Envelope_WithMillisAndLowercaseKeys()
    {
        var entries = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var deviceStatus = new DateTime(2024, 3, 26, 12, 5, 0, DateTimeKind.Utc);
        var profile = new DateTime(2024, 3, 20, 8, 0, 0, DateTimeKind.Utc);

        var statusService = new Mock<IStatusService>();
        statusService.Setup(s => s.GetLastModifiedAsync()).ReturnsAsync(new LastModifiedResponse
        {
            ServerTime = new DateTime(2024, 3, 26, 12, 6, 0, DateTimeKind.Utc),
            Entries = entries,
            DeviceStatus = deviceStatus,
            Profile = profile,
            Additional = new Dictionary<string, DateTime>
            {
                ["auth"] = new DateTime(2024, 3, 26, 12, 0, 0, DateTimeKind.Utc),
            },
        });

        var body = CaptureBody(await CreateController(statusService).GetLastModified());

        body.GetProperty("status").GetInt32().Should().Be(200);

        var result = body.GetProperty("result");
        result.GetProperty("srvDate").GetInt64()
            .Should().Be(new DateTimeOffset(2024, 3, 26, 12, 6, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());

        var collections = result.GetProperty("collections");
        collections.GetProperty("entries").GetInt64()
            .Should().Be(new DateTimeOffset(entries).ToUnixTimeMilliseconds());
        // Lowercase, singular Nightscout collection names.
        collections.GetProperty("devicestatus").GetInt64()
            .Should().Be(new DateTimeOffset(deviceStatus).ToUnixTimeMilliseconds());
        collections.GetProperty("profile").GetInt64()
            .Should().Be(new DateTimeOffset(profile).ToUnixTimeMilliseconds());
        // Internal keys are not sync collections.
        collections.TryGetProperty("auth", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetLastModified_OnServiceError_StillReturnsEnvelope()
    {
        var statusService = new Mock<IStatusService>();
        statusService.Setup(s => s.GetLastModifiedAsync()).ThrowsAsync(new InvalidOperationException("boom"));

        var body = CaptureBody(await CreateController(statusService).GetLastModified());

        body.GetProperty("status").GetInt32().Should().Be(200);
        var result = body.GetProperty("result");
        result.GetProperty("srvDate").GetInt64().Should().BeGreaterThan(0);
        result.GetProperty("collections").EnumerateObject().Should().BeEmpty();
    }
}
