using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

public class GoogleHealthGenericWriteTests
{
    [Theory]
    [InlineData("GoogleHealth", """{"enabled":false}""", "Disconnect it")]
    [InlineData("googlehealth", """{"Enabled":true}""", "Disconnect it")]
    [InlineData("GOOGLEHEALTH", """{"enabled":true,"lookbackDays":7}""", "Disconnect it")]
    [InlineData("GoogleHealth", """{"importFrom":"1999-12-31T00:00:00Z"}""", "/api/v4/google-health/options")]
    [InlineData("GoogleHealth", """{"importFrom":"2999-01-01T00:00:00Z"}""", "/api/v4/google-health/options")]
    [InlineData("googlehealth", """{"lookbackDays":0}""", "/api/v4/google-health/options")]
    [InlineData("GoogleHealth", """{"lookbackDays":91}""", "/api/v4/google-health/options")]
    [InlineData("GoogleHealth", """{"lookbackDays":7}""", "/api/v4/google-health/options")]
    public async Task The_generic_write_refuses_google_health_options(string connectorName, string body, string detail)
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await SaveAsync(service, connectorName, body);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain(detail);
        service.Verify(s => s.SaveConfigurationAsync(
            It.IsAny<string>(), It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_generic_write_still_accepts_an_enabled_flag_for_other_connectors()
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await SaveAsync(service, "Dexcom", """{"enabled":false}""");

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.SaveConfigurationAsync(
            "Dexcom", It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("GoogleHealth")]
    [InlineData("googlehealth")]
    public async Task The_secrets_write_refuses_google_health(string connectorName)
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await NewController(service).SaveSecrets(
            connectorName, new Dictionary<string, string> { ["refreshToken"] = "forged" }, CancellationToken.None);

        AssertGrantRefusal(result);
        service.Verify(s => s.SaveSecretsAsync(
            It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_secrets_write_still_works_for_other_connectors()
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await NewController(service).SaveSecrets(
            "Dexcom", new Dictionary<string, string> { ["password"] = "p" }, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        service.Verify(s => s.SaveSecretsAsync(
            "Dexcom", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("GoogleHealth")]
    [InlineData("googlehealth")]
    public async Task The_delete_refuses_google_health(string connectorName)
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await NewController(service).DeleteConfiguration(connectorName, CancellationToken.None);

        AssertGrantRefusal(result);
        service.Verify(s => s.DeleteConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_delete_still_works_for_other_connectors()
    {
        var service = new Mock<IConnectorConfigurationService>();
        service.Setup(s => s.DeleteConfigurationAsync("Dexcom", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await NewController(service).DeleteConfiguration("Dexcom", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Theory]
    [InlineData("GoogleHealth", true)]
    [InlineData("googlehealth", false)]
    public async Task The_active_toggle_refuses_google_health(string connectorName, bool isActive)
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await NewController(service)
            .SetActive(connectorName, new SetActiveRequest { IsActive = isActive }, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Verify(s => s.SetActiveAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_active_toggle_still_works_for_other_connectors()
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await NewController(service)
            .SetActive("Dexcom", new SetActiveRequest { IsActive = false }, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        service.Verify(s => s.SetActiveAsync(
            "Dexcom", false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static void AssertGrantRefusal(IActionResult result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain("/api/v4/google-health");
    }

    private static async Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(
        Mock<IConnectorConfigurationService> service, string connectorName, string body)
    {
        service
            .Setup(s => s.GetSchemaAsync(connectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("{}"));
        service
            .Setup(s => s.SaveConfigurationAsync(
                connectorName, It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse { ConnectorName = connectorName });
        var controller = NewController(service);

        return await controller.SaveConfiguration(
            connectorName, JsonDocument.Parse(body), CancellationToken.None);
    }

    private static ConfigurationController NewController(Mock<IConnectorConfigurationService> service) =>
        new(service.Object, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
}
