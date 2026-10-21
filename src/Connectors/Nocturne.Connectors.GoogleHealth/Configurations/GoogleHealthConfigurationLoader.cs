using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.GoogleHealth.Configurations;

/// <summary>
/// Reports every tenant's connector as disabled while the instance has no Google client, so
/// scheduled polling skips it quietly instead of failing each tenant's sync and marking it
/// unhealthy for something only the operator can fix.
/// </summary>
public sealed class GoogleHealthConfigurationLoader(
    ConnectorConfigurationLoader<GoogleHealthConnectorConfiguration> inner,
    IOptions<GoogleHealthPlatformOptions> platform)
    : IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>
{
    public async Task<GoogleHealthConnectorConfiguration> LoadForTenantAsync(CancellationToken ct)
    {
        var configuration = await inner.LoadForTenantAsync(ct);
        if (!platform.Value.IsConfigured) configuration.Enabled = false;
        return configuration;
    }
}
