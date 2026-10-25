using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.GoogleHealth.Configurations;

/// <summary>
/// Enables a tenant's connector exactly while it holds a refresh token its instance's Google
/// client can use, so the stored grant alone decides whether it syncs. While the instance has no
/// Google client every tenant reads as disabled, so scheduled polling skips it quietly instead of
/// failing each tenant's sync and marking it unhealthy for something only the operator can fix.
/// </summary>
public sealed class GoogleHealthConfigurationLoader(
    ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> inner,
    IOptions<GoogleHealthPlatformOptions> platform)
    : IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>
{
    public async Task<GoogleHealthConnectorConfiguration> LoadForTenantAsync(CancellationToken ct)
    {
        var configuration = await inner.LoadForTenantAsync(ct);
        configuration.Enabled = platform.Value.IsConfigured &&
            !string.IsNullOrWhiteSpace(configuration.RefreshToken) &&
            !configuration.IssuedToAnotherClient(platform.Value);
        return configuration;
    }
}
