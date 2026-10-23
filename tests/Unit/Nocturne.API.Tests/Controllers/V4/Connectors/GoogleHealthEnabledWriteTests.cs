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

public class GoogleHealthEnabledWriteTests
{
    [Theory]
    [InlineData("GoogleHealth", """{"enabled":false}""")]
    [InlineData("googlehealth", """{"Enabled":true}""")]
    [InlineData("GOOGLEHEALTH", """{"enabled":true,"lookbackDays":7}""")]
    public async Task The_generic_write_refuses_an_enabled_flag_for_google_health(string connectorName, string body)
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await SaveAsync(service, connectorName, body);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        service.Verify(s => s.SaveConfigurationAsync(
            It.IsAny<string>(), It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_generic_write_still_accepts_other_google_health_settings()
    {
        var service = new Mock<IConnectorConfigurationService>();

        var result = await SaveAsync(service, "GoogleHealth", """{"lookbackDays":7}""");

        result.Result.Should().BeOfType<OkObjectResult>();
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
