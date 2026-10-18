using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Configurations;

public class GoogleHealthConfigurationLoaderTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task An_enabled_tenant_stays_enabled_only_while_the_instance_has_a_client(
        bool platformConfigured, bool enabled)
    {
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(service => service.GetConfigurationAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "GoogleHealth",
                Configuration = JsonDocument.Parse("""{"enabled":true}""")
            });
        configurations.Setup(service => service.GetSecretsAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["refreshToken"] = "refresh" });
        var loader = new GoogleHealthConfigurationLoader(
            new ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>(
                new ConnectorRegistration<GoogleHealthConnectorConfiguration>(new(), "GoogleHealth"),
                configurations.Object,
                NullLogger<ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>>.Instance),
            platformConfigured ? TestPlatform.Options() : Options.Create(new GoogleHealthPlatformOptions()));

        var configuration = await loader.LoadForTenantAsync(default);

        configuration.Enabled.Should().Be(enabled);
    }
}
