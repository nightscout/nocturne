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

/// <summary>
/// The Nightscout, remote-Nocturne and MyLife connectors declare <c>format: "uri"</c> on their
/// base-URL property, and the schema validator never enforced it — it covered type, minimum,
/// maximum and enum only. Anything at all could be stored, including a non-http scheme and a
/// string that is not a URL, which then failed at sync time rather than at the write.
/// </summary>
/// <remarks>
/// This is the write-side half. It deliberately does not judge what address the URL resolves to:
/// a self-hosted deployment legitimately points a connector at a private address, and the
/// narrower rule that no connector may reach a link-local address is enforced at the fetch
/// instead, where a row stored by some other path is covered too.
/// </remarks>
public class ConnectorConfigurationUriValidationTests
{
    private const string ConnectorName = "Nightscout";

    [Theory]
    [InlineData("https://mysite.example")]
    [InlineData("http://mysite.example:1337")]
    [InlineData("mysite.example")]                 // bare host: the connector supplies https://
    [InlineData("mysite.example:1337")]            // bare host with port: Nightscout's default self-hosted port
    [InlineData("localhost:1337")]                 // same, and parses as scheme "localhost"
    [InlineData("http://nightscout:1337")]         // sibling container
    [InlineData("http://192.168.1.50:1337")]       // LAN
    public async Task SaveConfiguration_AcceptsAUsableUrl(string url)
    {
        var result = await SaveAsync(url);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>(
            "a value the connector would normalise and use must not be rejected at the write");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveConfiguration_RejectsSomethingThatIsNotAnHttpUrl(string url)
    {
        var result = await SaveAsync(url);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SaveConfiguration_RejectsCredentialsEmbeddedInTheUrl()
    {
        // Runtime configuration is returned in the clear by GET; the secret fields are the ones
        // held back, so a password smuggled into the URL would sidestep that.
        var result = await SaveAsync("https://admin:hunter2@mysite.example");

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    private static async Task<ActionResult<ConnectorConfigurationResponse>> SaveAsync(string url)
    {
        var schema = JsonDocument.Parse("""
            {
              "properties": {
                "url": { "type": "string", "format": "uri" }
              }
            }
            """);

        var service = new Mock<IConnectorConfigurationService>();
        service
            .Setup(s => s.GetSchemaAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(schema);
        service
            .Setup(s => s.SaveConfigurationAsync(
                ConnectorName, It.IsAny<JsonDocument>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse { ConnectorName = ConnectorName });

        var controller = new ConfigurationController(
            service.Object, NullLogger<ConfigurationController>.Instance)
        {
            // SaveConfiguration reads User.Identity for the audit field.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var configuration = JsonDocument.Parse(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["enabled"] = true, ["url"] = url }));

        return await controller.SaveConfiguration(ConnectorName, configuration, CancellationToken.None);
    }
}
