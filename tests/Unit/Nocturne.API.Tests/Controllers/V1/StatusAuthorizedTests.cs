using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// <c>status.json?token=</c> carries the access-token exchange's result in <c>authorized</c>, as
/// Nightscout does, while the status itself stays one cached instance shared by every caller.
/// </summary>
[Trait("Category", "Unit")]
public class StatusAuthorizedTests
{
    private const string ValidToken = "dashboard-0123456789abcdef";

    private static readonly AuthorizationResponse Exchanged = new()
    {
        Token = "header.payload.signature",
        Sub = "Dashboard",
        Iat = 1_700_000_000,
        Exp = 1_700_028_800,
        PermissionGroups = [["api:entries:read"]],
    };

    private readonly StatusResponse _cached = new() { Status = "ok", Name = "nightscout" };
    private readonly Mock<IAuthorizationService> _exchange = new();

    public StatusAuthorizedTests()
    {
        _exchange.Setup(e => e.GenerateJwtFromAccessTokenAsync(ValidToken)).ReturnsAsync(Exchanged);
        _exchange.Setup(e => e.GenerateJwtFromAccessTokenAsync(It.Is<string>(t => t != ValidToken)))
            .ReturnsAsync((AuthorizationResponse?)null);
    }

    [Fact]
    public async Task AValidToken_PutsTheExchangeResultInAuthorized()
    {
        var (status, controller) = await GetJsonAsync($"?token={ValidToken}");

        status.Authorized.Should().BeSameAs(Exchanged);
        var body = JsonSerializer.SerializeToElement(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var authorized = body.GetProperty("authorized");
        authorized.GetProperty("token").GetString().Should().Be(Exchanged.Token);
        authorized.GetProperty("sub").GetString().Should().Be("Dashboard");
        authorized.GetProperty("iat").GetInt64().Should().Be(Exchanged.Iat);
        authorized.GetProperty("exp").GetInt64().Should().Be(Exchanged.Exp);
        authorized.GetProperty("permissionGroups")[0][0].GetString().Should().Be("api:entries:read");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task AValidToken_LeavesTheCachedStatusUntouched()
    {
        var (status, _) = await GetJsonAsync($"?token={ValidToken}");

        status.Should().NotBeSameAs(_cached);
        _cached.Authorized.Should().BeNull();
    }

    [Fact]
    public async Task AnInvalidToken_AnswersTheNormalStatusWithAuthorizedNull()
    {
        var (status, controller) = await GetJsonAsync("?token=not-a-token");

        status.Should().BeSameAs(_cached);
        status.Authorized.Should().BeNull();
        controller.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task NoToken_RunsNoExchange()
    {
        var (status, _) = await GetJsonAsync("");

        status.Should().BeSameAs(_cached);
        _exchange.Verify(e => e.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AFixedBodyFormat_RunsNoExchange()
    {
        var controller = Controller("?token=" + ValidToken, "text/plain");

        await controller.GetStatus();

        _exchange.Verify(e => e.GenerateJwtFromAccessTokenAsync(It.IsAny<string>()), Times.Never);
    }

    private async Task<(StatusResponse Status, StatusController Controller)> GetJsonAsync(string query)
    {
        var controller = Controller(query, "application/json");
        var result = await controller.GetStatus();
        var json = result.Should().BeOfType<JsonResult>().Subject;
        return (json.Value.Should().BeOfType<StatusResponse>().Subject, controller);
    }

    private StatusController Controller(string query, string accept)
    {
        var statusService = new Mock<IStatusService>();
        statusService.Setup(s => s.GetSystemStatusAsync()).ReturnsAsync(_cached);

        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        http.Request.Headers.Accept = accept;

        return new StatusController(statusService.Object, _exchange.Object, NullLogger<StatusController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
