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
    [InlineData(true, TestPlatform.ClientId, "refresh", true)]
    [InlineData(true, TestPlatform.ClientId, "", false)]
    [InlineData(true, null, "refresh", false)]
    [InlineData(true, "tenant.apps.googleusercontent.com", "refresh", false)]
    [InlineData(false, TestPlatform.ClientId, "refresh", false)]
    public async Task A_tenant_syncs_only_while_it_holds_a_token_its_instance_client_issued(
        bool platformConfigured, string? sessionClientId, string refreshToken, bool enabled)
    {
        var secrets = new Dictionary<string, string> { ["refreshToken"] = refreshToken };
        if (sessionClientId is not null) secrets["clientId"] = sessionClientId;
        var loader = Loader(JsonDocument.Parse("""{"enabled":false}"""), secrets, platformConfigured);

        var configuration = await loader.LoadForTenantAsync(default);

        configuration.Enabled.Should().Be(enabled);
    }

    [Fact]
    public async Task A_stored_enabled_flag_does_not_override_the_grant()
    {
        var loader = Loader(
            JsonDocument.Parse("""{"enabled":true}"""), new Dictionary<string, string>(), platformConfigured: true);

        (await loader.LoadForTenantAsync(default)).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_row_with_no_configuration_still_syncs_on_its_grant()
    {
        var loader = Loader(
            null,
            new Dictionary<string, string> { ["refreshToken"] = "refresh", ["clientId"] = TestPlatform.ClientId },
            platformConfigured: true);

        (await loader.LoadForTenantAsync(default)).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_tenant_with_no_row_stays_disabled()
    {
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(service => service.GetConfigurationAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorConfigurationResponse?)null);

        var loader = Loader(configurations, platformConfigured: true);

        (await loader.LoadForTenantAsync(default)).Enabled.Should().BeFalse();
    }

    private static GoogleHealthConfigurationLoader Loader(
        JsonDocument? configuration, Dictionary<string, string> secrets, bool platformConfigured)
    {
        var configurations = new Mock<IConnectorConfigurationService>();
        configurations.Setup(service => service.GetConfigurationAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = "GoogleHealth",
                Configuration = configuration!
            });
        configurations.Setup(service => service.GetSecretsAsync("GoogleHealth", It.IsAny<CancellationToken>()))
            .ReturnsAsync(secrets);
        return Loader(configurations, platformConfigured);
    }

    private static GoogleHealthConfigurationLoader Loader(
        Mock<IConnectorConfigurationService> configurations, bool platformConfigured) =>
        new(
            new ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>(
                new ConnectorRegistration<GoogleHealthConnectorConfiguration>(new(), "GoogleHealth"),
                configurations.Object,
                NullLogger<ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>>.Instance),
            platformConfigured ? TestPlatform.Options() : Options.Create(new GoogleHealthPlatformOptions()));
}
