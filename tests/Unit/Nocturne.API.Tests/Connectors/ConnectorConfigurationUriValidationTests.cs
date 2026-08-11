using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Connectors;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// What a <c>format: "uri"</c> connector field will accept into storage. Every connector taking a
/// member-supplied base URL declares that format, and the stored value is later prepended with
/// <c>https://</c> if it does not already start with <c>http</c>.
/// </summary>
/// <remarks>
/// The address a URL resolves to is judged at the sink, not here — this is about which strings are
/// allowed to become a base URL at all. The prepend is what makes the scheme test load-bearing: a
/// value that carries its own non-http scheme, or its own authority, must not be treated as a bare
/// host and glued behind <c>https://</c>.
/// </remarks>
public class ConnectorConfigurationUriValidationTests
{
    [Theory]
    [InlineData("https://mynightscout.example")]
    [InlineData("http://192.168.1.50:1337")]
    [InlineData("mysite.example")]
    [InlineData("mysite.example:1337")]
    [InlineData("localhost:1337")]
    [InlineData("mysite.example:1337/nightscout")]
    [InlineData("[fd12:3456:789a::1]:1337")]
    public async Task Accepts_AUrlOrABareHost(string candidate) =>
        (await Save(candidate)).Should().BeOfType<OkObjectResult>(
            "an existing tenant re-saving this value must not start failing");

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://mysite.example")]
    [InlineData("https://admin:hunter2@mysite.example")]
    [InlineData("admin:hunter2@mysite.example")]
    [InlineData("")]
    public async Task Refuses_ANonHttpSchemeOrEmbeddedCredentials(string candidate) =>
        (await Save(candidate)).Should().BeOfType<BadRequestObjectResult>();

    [Theory]
    [InlineData("file:/etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//169.254.169.254/latest/meta-data/")]
    [InlineData("/latest/meta-data/")]
    public async Task Refuses_ASchemeOrAuthorityThatCarriesNoDoubleSlash(string candidate) =>
        // Testing for "://" is what let the single-slash form through: prepending https:// to
        // "file:/etc/passwd" yields a URL with a host named 'file', which parses, so the value was
        // stored. The other three were already refused by the prepended parse failing — they are
        // here so the rule that replaced the "://" test is held to all of them, not just the one
        // that was getting through.
        (await Save(candidate)).Should().BeOfType<BadRequestObjectResult>();

    private static async Task<IActionResult?> Save(string candidate)
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetSchemaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse(
                """{"properties":{"baseUrl":{"type":"string","format":"uri"}}}"""));
        configService
            .Setup(s => s.SaveConfigurationAsync(
                It.IsAny<string>(), It.IsAny<JsonDocument>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse());

        var controller = new ConfigurationController(
            configService.Object, NullLogger<ConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var configuration = JsonDocument.Parse(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["baseUrl"] = candidate }));

        var result = await controller.SaveConfiguration("Nightscout", configuration, default);
        return result.Result;
    }
}
