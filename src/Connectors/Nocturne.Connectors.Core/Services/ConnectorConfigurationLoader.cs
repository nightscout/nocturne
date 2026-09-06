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
        if (config.Enabled)
        {
            var missing = config.MissingRequiredProperties();
            if (missing.Count > 0)
            {
                logger.LogDebug(
                    "{ConnectorName} is enabled but missing required configuration ({Missing}); skipping sync",
                    registration.ConnectorName, string.Join(", ", missing));
                config.Enabled = false;

                // The tenant turned this connector on, so silence is the wrong answer: skipping with
                // only a debug log leaves it reporting healthy while it never syncs at all.
                await ReportIncompleteConfigurationAsync(missing, ct);
            }
        }

        return config;
    }

    /// <summary>
    ///     Records the missing configuration as the connector's health state, so the tenant sees why
    ///     it is not syncing. Written only when it differs from what is already stored — this runs on
    ///     every poll cycle, and rewriting an unchanged message would be a needless write each time.
    /// </summary>
    private async Task ReportIncompleteConfigurationAsync(IReadOnlyList<string> missing, CancellationToken ct)
    {
        var message = $"Not syncing: {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} required "
                      + "but not configured.";

        try
        {
            var health = await configService.GetHealthStateAsync(registration.ConnectorName, ct);
            if (health is { IsHealthy: false } && health.LastErrorMessage == message)
                return;

            await configService.UpdateHealthStateAsync(
                registration.ConnectorName,
                lastErrorMessage: message,
                lastErrorAt: DateTime.UtcNow,
                isHealthy: false,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "Failed to record incomplete configuration for {ConnectorName}",
                registration.ConnectorName);
        }
    }

    private static TConfig CloneDefaults(TConfig source)
    {
        var json = JsonSerializer.Serialize(source, CloneOptions);
        return JsonSerializer.Deserialize<TConfig>(json, CloneOptions)!;
    }
}
