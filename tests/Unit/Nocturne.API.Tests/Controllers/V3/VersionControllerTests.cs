using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V3;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V3;

/// <summary>
/// <c>/api/v3/version</c> must use the Nightscout V3 envelope
/// <c>{ status, result: { version, apiVersion, srvDate, storage } }</c>; a client reading
/// <c>result.*</c> from the bare model gets nothing.
/// </summary>
[Trait("Category", "Unit")]
public class VersionControllerTests
{
    private static VersionController CreateController(Mock<IStatusService> statusService)
    {
        return new VersionController(statusService.Object, NullLogger<VersionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static JsonElement CaptureBody(ActionResult<V3Envelope<V3VersionResult>> result)
    {
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
    }

    [Fact]
    public async Task GetVersion_WrapsStatusValuesInV3Envelope()
    {
        var statusService = new Mock<IStatusService>();
        statusService
            .Setup(s => s.GetV3SystemStatusAsync())
            .ReturnsAsync(
                new V3StatusResponse
                {
                    Version = "0.2.7",
                    ApiVersion = "3.0.5",
                    SrvDate = 1_700_000_000_000,
                    Head = "4f3c9a1",
                    Storage = new StorageInfo { StorageType = "postgresql", Version = "16.4" },
                }
            );

        var body = CaptureBody(await CreateController(statusService).GetVersion());

        body.GetProperty("status").GetInt32().Should().Be(200);
        var result = body.GetProperty("result");
        result.GetProperty("version").GetString().Should().Be("0.2.7");
        result.GetProperty("apiVersion").GetString().Should().Be("3.0.5");
        result.GetProperty("srvDate").GetInt64().Should().Be(1_700_000_000_000);
        result.GetProperty("storage").GetProperty("storage").GetString().Should().Be("postgresql");
        result.GetProperty("storage").GetProperty("version").GetString().Should().Be("16.4");
    }

    [Fact]
    public async Task GetVersion_OnError_ReturnsTheSameEnvelopeShape()
    {
        var statusService = new Mock<IStatusService>();
        statusService
            .Setup(s => s.GetV3SystemStatusAsync())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var body = CaptureBody(await CreateController(statusService).GetVersion());

        body.GetProperty("status").GetInt32().Should().Be(200);
        var result = body.GetProperty("result");
        result.GetProperty("version").GetString().Should().Be(AppVersion.Current);
        result.GetProperty("apiVersion").GetString().Should().Be("3.0.5");
        result.GetProperty("srvDate").GetInt64().Should().BePositive();
        result.GetProperty("storage").GetProperty("storage").GetString().Should().NotBeNull();
    }
}
