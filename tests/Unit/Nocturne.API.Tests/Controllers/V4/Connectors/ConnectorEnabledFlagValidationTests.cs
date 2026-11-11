using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Connectors;

public class ConnectorEnabledFlagValidationTests
{
    private const string ConnectorName = "Dexcom";

    [Theory]
    [InlineData("""{"enabled":"off"}""")]
    [InlineData("""{"enabled":null}""")]
    [InlineData("""{"enabled":1}""")]
    public async Task SaveConfiguration_RefusesAFlagThatIsNotABoolean(string body)
    {
        var service = ServiceWith(out var saved);

        var result = await SaveAsync(service, body);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        saved.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"enabled":false}""")]
    public async Task SaveConfiguration_AcceptsABooleanFlag(string body)
    {
        var service = ServiceWith(out var saved);

        var result = await SaveAsync(service, body);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
        saved.Should().ContainSingle();
    }

    private static IConnectorConfigurationService ServiceWith(out List<JsonDocument> saved)
    {
        var schema = JsonDocument.Parse("""
            {
              "properties": {
                "enabled": { "type": "boolean" }
              }
            }
            """);
        var calls = new List<JsonDocument>();
        saved = calls;

        var service = new Mock<IConnectorConfigurationService>();
        service
            .Setup(s => s.GetSchemaAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(schema);
        service
            .Setup(s => s.SaveConfigurationAsync(
                ConnectorName, It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, JsonDocument, string?, CancellationToken>((_, document, _, _) => calls.Add(document))
            .ReturnsAsync(new ConnectorConfigurationResponse { ConnectorName = ConnectorName });
        return service.Object;
    }

    private static async Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(
        IConnectorConfigurationService service, string body)
    {
        var controller = new ConfigurationController(
            service, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return await controller.SaveConfiguration(ConnectorName, JsonDocument.Parse(body), CancellationToken.None);
    }
}
