using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Regression tests for <see cref="ConnectorConfigurationLoader{TConfig}"/>.
///     A connector must only run for a tenant that has actually configured it. The startup
///     defaults carry <c>Enabled = true</c> (a property initializer), so when a tenant has no
///     config row the loader must explicitly disable the connector — otherwise every connector
///     polls every tenant with empty credentials, producing auth failures and
///     "configuration not found" health-state noise across all tenants.
/// </summary>
public class ConnectorConfigurationLoaderTests
{
    private const string ConnectorName = "TestConnector";

    private static ConnectorConfigurationLoader<LoaderTestConfig> CreateLoader(
        Mock<IConnectorConfigurationService> configService)
    {
        var registration = new ConnectorRegistration<LoaderTestConfig>(new LoaderTestConfig(), ConnectorName);
        return new ConnectorConfigurationLoader<LoaderTestConfig>(
            registration,
            configService.Object,
            NullLogger<ConnectorConfigurationLoader<LoaderTestConfig>>.Instance);
    }

    [Fact]
    public async Task LoadForTenantAsync_DisablesConnector_WhenTenantHasNoConfigRow()
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectorConfigurationResponse?)null);
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var config = await CreateLoader(configService).LoadForTenantAsync(CancellationToken.None);

        config.Enabled.Should().BeFalse(
            "a connector with no per-tenant config row must not run, even though the defaults set Enabled = true");
    }

    [Fact]
    public async Task LoadForTenantAsync_LoadsConfig_WhenTenantHasConfigRow()
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = ConnectorName,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"username\": \"user@example.com\"}")
            });
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var config = await CreateLoader(configService).LoadForTenantAsync(CancellationToken.None);

        config.Enabled.Should().BeTrue("a configured, enabled tenant must still sync");
        config.Username.Should().Be("user@example.com", "the stored configuration must be applied");
    }

    [Fact]
    public async Task LoadForTenantAsync_DisablesConnector_WhenRequiredSecretMissing()
    {
        // The connector has an enabled config row (e.g. enabled via the UI toggle, or its
        // non-secret config saved) but its required secret was never provided.
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = ConnectorName,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"url\": \"https://ns.example.com\"}")
            });
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var config = await CreateRequiredSecretLoader(configService).LoadForTenantAsync(CancellationToken.None);

        config.Enabled.Should().BeFalse(
            "a connector enabled without its required credentials must not sync — it would fail authentication every cycle");
    }

    [Fact]
    public async Task LoadForTenantAsync_KeepsConnectorEnabled_WhenRequiredSecretProvided()
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = ConnectorName,
                Configuration = JsonDocument.Parse("{\"enabled\": true, \"url\": \"https://ns.example.com\"}")
            });
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["apiSecret"] = "s3cr3t" });

        var config = await CreateRequiredSecretLoader(configService).LoadForTenantAsync(CancellationToken.None);

        config.Enabled.Should().BeTrue("a connector with its required credentials present must sync");
        config.ApiSecret.Should().Be("s3cr3t", "the required secret must be applied from the secret store");
    }

    /// <summary>
    ///     Skipping an incomplete connector is right, but doing it silently is not: the tenant turned
    ///     it on, and with only a debug log it reports healthy forever while never syncing once. A
    ///     CareLink connector sat in exactly that state for seven weeks because its username was blank.
    /// </summary>
    [Fact]
    public async Task LoadForTenantAsync_RecordsWhyItIsNotSyncing_WhenRequiredConfigurationMissing()
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = ConnectorName,
                Configuration = JsonDocument.Parse("{\"enabled\": true}")
            });
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        configService
            .Setup(s => s.GetHealthStateAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorHealthStateDto { IsHealthy = true });

        await CreateRequiredSecretLoader(configService).LoadForTenantAsync(CancellationToken.None);

        configService.Verify(s => s.UpdateHealthStateAsync(
                ConnectorName,
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.Is<string>(m => m.Contains("ApiSecret") && m.Contains("required")),
                It.IsAny<DateTime?>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the tenant must be told which setting is missing, not left looking healthy");
    }

    /// <summary>
    ///     The loader runs every poll cycle, so an unchanged message must not be rewritten each time.
    /// </summary>
    [Fact]
    public async Task LoadForTenantAsync_DoesNotRewriteHealthState_WhenAlreadyRecorded()
    {
        var configService = new Mock<IConnectorConfigurationService>();
        configService
            .Setup(s => s.GetConfigurationAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse
            {
                ConnectorName = ConnectorName,
                Configuration = JsonDocument.Parse("{\"enabled\": true}")
            });
        configService
            .Setup(s => s.GetSecretsAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        configService
            .Setup(s => s.GetHealthStateAsync(ConnectorName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorHealthStateDto
            {
                IsHealthy = false,
                LastErrorMessage = "Not syncing: ApiSecret is required but not configured."
            });

        await CreateRequiredSecretLoader(configService).LoadForTenantAsync(CancellationToken.None);

        configService.Verify(s => s.UpdateHealthStateAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string>(),
                It.IsAny<DateTime?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static ConnectorConfigurationLoader<RequiredSecretTestConfig> CreateRequiredSecretLoader(
        Mock<IConnectorConfigurationService> configService)
    {
        var registration = new ConnectorRegistration<RequiredSecretTestConfig>(new RequiredSecretTestConfig(), ConnectorName);
        return new ConnectorConfigurationLoader<RequiredSecretTestConfig>(
            registration,
            configService.Object,
            NullLogger<ConnectorConfigurationLoader<RequiredSecretTestConfig>>.Instance);
    }

    private sealed class LoaderTestConfig : BaseConnectorConfiguration
    {
        public LoaderTestConfig() => ConnectSource = ConnectSource.Dexcom;

        public string Username { get; set; } = string.Empty;

        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class RequiredSecretTestConfig : BaseConnectorConfiguration
    {
        public RequiredSecretTestConfig() => ConnectSource = ConnectSource.Nightscout;

        [ConnectorProperty(ConnectorPropertyKey.ApiSecret, Required = true, Secret = true)]
        public string ApiSecret { get; set; } = string.Empty;

        protected override void ValidateSourceSpecificConfiguration() { }
    }
}
