using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

public class ConnectorConfigurationLoader<TConfig>(
    IConnectorRegistration<TConfig> registration,
    ILogger<ConnectorConfigurationLoader<TConfig>> logger)
    : IConnectorConfigurationLoader<TConfig>
    where TConfig : BaseConnectorConfiguration, new()
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<TConfig> LoadForTenantAsync(IServiceProvider scopeProvider, CancellationToken ct)
    {
        // Start from a fresh copy of the startup defaults
        var config = CloneDefaults(registration.Defaults);

        try
        {
            var configService = scopeProvider.GetRequiredService<IConnectorConfigurationService>();

            var dbConfig = await configService.GetConfigurationAsync(registration.ConnectorName, ct);
            if (dbConfig?.Configuration != null)
                ConnectorConfigurationBinder.ApplyJsonToConfig(dbConfig.Configuration, config);

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

        return config;
    }

    private static TConfig CloneDefaults(TConfig source)
    {
        var json = JsonSerializer.Serialize(source, CloneOptions);
        return JsonSerializer.Deserialize<TConfig>(json, CloneOptions)!;
    }
}
