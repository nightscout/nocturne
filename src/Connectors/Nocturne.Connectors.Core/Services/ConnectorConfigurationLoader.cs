using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

public class ConnectorConfigurationLoader<TConfig>(
    IConnectorRegistration<TConfig> registration,
    IConnectorConfigurationService configService,
    ILogger<ConnectorConfigurationLoader<TConfig>> logger)
    : IConnectorConfigurationLoader<TConfig>
    where TConfig : BaseConnectorConfiguration, new()
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<TConfig> LoadForTenantAsync(CancellationToken ct)
    {
        // Start from a fresh copy of the startup defaults
        var config = CloneDefaults(registration.Defaults);

        try
        {
            var dbConfig = await configService.GetConfigurationAsync(registration.ConnectorName, ct);
            if (dbConfig?.Configuration != null)
            {
                ConnectorConfigurationBinder.ApplyJsonToConfig(dbConfig.Configuration, config);
            }
            else
            {
                // No per-tenant configuration row exists, so this connector is not configured for
                // this tenant and must not sync. registration.Defaults sets Enabled = true (a C#
                // property initializer, not a deliberate opt-in); without this, every connector
                // would poll every tenant with empty credentials — producing auth failures and
                // "configuration not found" health-state noise across all tenants.
                config.Enabled = false;
            }

            var secrets = await configService.GetSecretsAsync(registration.ConnectorName, ct);
            if (secrets.Count > 0)
                ConnectorConfigurationBinder.ApplySecretsToConfig(secrets, config);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "Failed to load database configuration for {ConnectorName}",
                registration.ConnectorName);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Failed to load database configuration for {ConnectorName}",
                registration.ConnectorName);
        }

        // A connector can have a config row that is enabled yet missing its required credentials —
        // enabled via the UI toggle, saved before secrets were entered, or a required secret later
        // removed. Required secrets have been merged above, so if the connector is still missing
        // required configuration, syncing it would authenticate with empty credentials and fail
        // every cycle. Treat incomplete configuration as not configured and skip it, exactly like a
        // tenant that never configured the connector at all.
        if (config.Enabled && !config.HasRequiredConfiguration())
        {
            logger.LogDebug(
                "{ConnectorName} is enabled but missing required configuration; skipping sync",
                registration.ConnectorName);
            config.Enabled = false;
        }

        return config;
    }

    private static TConfig CloneDefaults(TConfig source)
    {
        var json = JsonSerializer.Serialize(source, CloneOptions);
        return JsonSerializer.Deserialize<TConfig>(json, CloneOptions)!;
    }
}
