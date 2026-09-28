using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Migration;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.TenantAdmin;

/// <summary>
/// A Nightscout URL is accepted as the connector accepts it, a bare host included, and anything
/// that is not an http or https address is refused before a job starts.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationControllerUrlTests
{
    private readonly Mock<IMigrationJobService> _migrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectors = new();

    public MigrationControllerUrlTests()
    {
        _migrations
            .Setup(m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MigrationJobInfo { Id = Guid.CreateVersion7(), Mode = MigrationMode.Api, CreatedAt = DateTime.UtcNow });
        _connectors
            .Setup(c => c.GetSecretsAsync("nightscout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
    }

    private MigrationController Controller() =>
        new(_migrations.Object, _connectors.Object, Mock.Of<ITenantAccessor>(), NullLogger<MigrationController>.Instance);

    private void ConnectorUrl(string url) =>
        _connectors
            .Setup(c => c.GetConfigurationAsync("nightscout", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "nightscout",
                Configuration = JsonDocument.Parse(JsonSerializer.Serialize(new { url })),
            });

    private static void ShouldBeRefused(IActionResult? result)
    {
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be(NightscoutBaseUri.InvalidUrlMessage);
    }

    [Theory]
    [InlineData("ftp://ns.example/nightscout")]
    [InlineData("/nightscout")]
    public async Task StartMigration_refuses_an_address_that_is_not_http(string url)
    {
        var response = await Controller().StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = url }, CancellationToken.None);

        ShouldBeRefused(response.Result);
        _migrations.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ns.example/nightscout")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token")]
    public async Task StartMigration_accepts_what_the_connector_accepts(string url)
    {
        var response = await Controller().StartMigration(
            new StartMigrationRequest { Mode = MigrationMode.Api, NightscoutUrl = url }, CancellationToken.None);

        response.Result.Should().BeOfType<AcceptedAtActionResult>();
    }

    [Fact]
    public async Task StartFromConnector_refuses_a_saved_address_that_is_not_http()
    {
        ConnectorUrl("ftp://ns.example/nightscout");

        var response = await Controller().StartFromConnector("nightscout", CancellationToken.None);

        ShouldBeRefused(response.Result);
        _migrations.Verify(
            m => m.StartMigrationAsync(It.IsAny<StartMigrationRequest>(), It.IsAny<TenantContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartFromConnector_starts_from_a_saved_bare_host()
    {
        ConnectorUrl("ns.example/nightscout");

        var response = await Controller().StartFromConnector("nightscout", CancellationToken.None);

        response.Result.Should().BeOfType<AcceptedAtActionResult>();
        _migrations.Verify(m => m.StartMigrationAsync(
            It.Is<StartMigrationRequest>(r => r.NightscoutUrl == "ns.example/nightscout"),
            It.IsAny<TenantContext?>(),
            It.IsAny<CancellationToken>()));
    }
}
