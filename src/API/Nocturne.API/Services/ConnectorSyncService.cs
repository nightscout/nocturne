using System.Reflection;
using System.Text.Json;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Connectors.MyFitnessPal.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Services;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Services;

/// <summary>
/// Dispatches manual sync requests to the correct connector service by name.
/// </summary>
public interface IConnectorSyncService
{
    Task<SyncResult> TriggerSyncAsync(
        string connectorId,
        SyncRequest request,
        CancellationToken ct
    );
}

/// <summary>
/// Resolves the concrete connector service by name and executes a sync.
/// Follows the same scope/resolve pattern as the connector background services.
/// </summary>
public class ConnectorSyncService : IConnectorSyncService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConnectorSyncService> _logger;

    public ConnectorSyncService(
        IServiceProvider serviceProvider,
        ILogger<ConnectorSyncService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<SyncResult> TriggerSyncAsync(
        string connectorId,
        SyncRequest request,
        CancellationToken ct
    )
    {
        _logger.LogInformation("Manual sync triggered for connector {ConnectorId}", connectorId);

        try
        {
            var result = connectorId.ToLowerInvariant() switch
            {
                "dexcom" => await ExecuteSyncAsync<
                    DexcomConnectorService,
                    DexcomConnectorConfiguration
                >("Dexcom", request, ct),
                "tidepool" => await ExecuteSyncAsync<
                    TidepoolConnectorService,
                    TidepoolConnectorConfiguration
                >("Tidepool", request, ct),
                "librelinkup" => await ExecuteSyncAsync<
                    LibreConnectorService,
                    LibreLinkUpConnectorConfiguration
                >("LibreLinkUp", request, ct),
                "glooko" => await ExecuteSyncAsync<
                    GlookoConnectorService,
                    GlookoConnectorConfiguration
                >("Glooko", request, ct),
                "mylife" => await ExecuteSyncAsync<
                    MyLifeConnectorService,
                    MyLifeConnectorConfiguration
                >("MyLife", request, ct),
                "myfitnesspal" => await ExecuteSyncAsync<
                    MyFitnessPalConnectorService,
                    MyFitnessPalConnectorConfiguration
                >("MyFitnessPal", request, ct),
                "nightscout" => await ExecuteSyncAsync<
                    NightscoutConnectorService,
                    NightscoutConnectorConfiguration
                >("Nightscout", request, ct),
                _ => new SyncResult
                {
                    Success = false,
                    Message = $"Unknown connector: {connectorId}",
                },
            };

            _logger.LogInformation(
                "Manual sync for {ConnectorId} completed: Success={Success}, Message={Message}",
                connectorId,
                result.Success,
                result.Message
            );

            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No service for type"))
        {
            _logger.LogWarning(
                "Connector {ConnectorId} is not registered (likely disabled)",
                connectorId
            );
            return new SyncResult
            {
                Success = false,
                Message = $"Connector '{connectorId}' is not configured or is disabled",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error during manual sync for connector {ConnectorId}",
                connectorId
            );
            return new SyncResult { Success = false, Message = $"Sync failed: {ex.Message}" };
        }
    }

    private async Task<SyncResult> ExecuteSyncAsync<TService, TConfig>(
        string connectorName,
        SyncRequest request,
        CancellationToken ct
    )
        where TService : class, IConnectorService<TConfig>
        where TConfig : class, IConnectorConfiguration
    {
        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        var config = scope.ServiceProvider.GetRequiredService<TConfig>();

        await LoadDatabaseConfigurationAsync(scope.ServiceProvider, connectorName, config, ct);

        return await service.SyncDataAsync(request, config, ct);
    }

    /// <summary>
    /// Loads runtime configuration and secrets from the database and applies them
    /// to the config singleton. Ensures DB-stored values (including encrypted
    /// passwords) are available for manual sync requests.
    /// </summary>
    private async Task LoadDatabaseConfigurationAsync<TConfig>(
        IServiceProvider scopedProvider,
        string connectorName,
        TConfig config,
        CancellationToken ct)
        where TConfig : class, IConnectorConfiguration
    {
        try
        {
            var configService = scopedProvider.GetRequiredService<IConnectorConfigurationService>();

            var dbConfig = await configService.GetConfigurationAsync(connectorName, ct);
            if (dbConfig?.Configuration != null)
            {
                ApplyJsonToConfig(dbConfig.Configuration, config);
            }

            var secrets = await configService.GetSecretsAsync(connectorName, ct);
            if (secrets.Count > 0)
            {
                ApplySecretsToConfig(secrets, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load database configuration for {ConnectorName} during manual sync",
                connectorName);
        }
    }

    private static void ApplyJsonToConfig<TConfig>(JsonDocument configuration, TConfig config)
        where TConfig : class
    {
        var properties = config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var root = configuration.RootElement;

        foreach (var property in properties)
        {
            if (!property.CanWrite) continue;

            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (!root.TryGetProperty(camelName, out var element)) continue;

            try
            {
                if (property.PropertyType == typeof(string) && element.ValueKind == JsonValueKind.String)
                    property.SetValue(config, element.GetString());
                else if (property.PropertyType == typeof(int) && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(config, element.GetInt32());
                else if (property.PropertyType == typeof(double) && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(config, element.GetDouble());
                else if (property.PropertyType == typeof(bool) &&
                         (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
                    property.SetValue(config, element.GetBoolean());
            }
            catch (Exception)
            {
                // Skip properties that can't be set
            }
        }
    }

    private static void ApplySecretsToConfig<TConfig>(Dictionary<string, string> secrets, TConfig config)
        where TConfig : class
    {
        var properties = config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            if (!property.CanWrite || property.PropertyType != typeof(string)) continue;

            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (secrets.TryGetValue(camelName, out var value))
            {
                property.SetValue(config, value);
            }
        }
    }
}
