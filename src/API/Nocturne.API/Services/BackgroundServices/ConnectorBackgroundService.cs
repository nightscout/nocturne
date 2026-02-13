using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Base class for connector background services that run within the API
/// </summary>
/// <typeparam name="TConfig">The connector configuration type</typeparam>
public abstract class ConnectorBackgroundService<TConfig> : BackgroundService
    where TConfig : class, IConnectorConfiguration
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly ILogger Logger;
    protected readonly TConfig Config;

    protected ConnectorBackgroundService(
        IServiceProvider serviceProvider,
        TConfig config,
        ILogger logger
    )
    {
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the connector name for logging
    /// </summary>
    protected abstract string ConnectorName { get; }

    /// <summary>
    /// Performs a single sync operation using the connector service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if sync was successful, false otherwise</returns>
    protected abstract Task<bool> PerformSyncAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads runtime configuration and secrets from the database and applies them
    /// to the Config singleton. This ensures DB-stored values (including encrypted
    /// passwords) are available to the connector at runtime.
    /// </summary>
    protected async Task LoadDatabaseConfigurationAsync(CancellationToken ct)
    {
        try
        {
            using var scope = ServiceProvider.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConnectorConfigurationService>();

            // Load runtime configuration from DB
            var dbConfig = await configService.GetConfigurationAsync(ConnectorName, ct);
            if (dbConfig?.Configuration != null)
            {
                ApplyJsonToConfig(dbConfig.Configuration);
                Logger.LogDebug("Applied database configuration for {ConnectorName}", ConnectorName);
            }

            // Load and decrypt secrets from DB
            var secrets = await configService.GetSecretsAsync(ConnectorName, ct);
            if (secrets.Count > 0)
            {
                ApplySecretsToConfig(secrets);
                Logger.LogDebug("Applied {Count} secrets for {ConnectorName}", secrets.Count, ConnectorName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to load database configuration for {ConnectorName}, using environment/startup values",
                ConnectorName);
        }
    }

    /// <summary>
    /// Applies JSON configuration values to the Config object via reflection.
    /// Matches camelCase JSON keys to PascalCase C# properties.
    /// </summary>
    private void ApplyJsonToConfig(JsonDocument configuration)
    {
        var properties = Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var root = configuration.RootElement;

        foreach (var property in properties)
        {
            if (!property.CanWrite) continue;

            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (!root.TryGetProperty(camelName, out var element)) continue;

            try
            {
                if (property.PropertyType == typeof(string) && element.ValueKind == JsonValueKind.String)
                    property.SetValue(Config, element.GetString());
                else if (property.PropertyType == typeof(int) && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(Config, element.GetInt32());
                else if (property.PropertyType == typeof(double) && element.ValueKind == JsonValueKind.Number)
                    property.SetValue(Config, element.GetDouble());
                else if (property.PropertyType == typeof(bool) &&
                         (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False))
                    property.SetValue(Config, element.GetBoolean());
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Could not apply config property {Property} for {ConnectorName}",
                    property.Name, ConnectorName);
            }
        }
    }

    /// <summary>
    /// Applies decrypted secret values to the Config object via reflection.
    /// Matches camelCase secret keys to PascalCase C# properties.
    /// </summary>
    private void ApplySecretsToConfig(Dictionary<string, string> secrets)
    {
        var properties = Config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            if (!property.CanWrite || property.PropertyType != typeof(string)) continue;

            var camelName = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (secrets.TryGetValue(camelName, out var value))
            {
                property.SetValue(Config, value);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load configuration and secrets from DB before checking enabled state
        await LoadDatabaseConfigurationAsync(stoppingToken);

        if (!Config.Enabled)
        {
            Logger.LogInformation(
                "{ConnectorName} connector is disabled, background service will not run",
                ConnectorName
            );
            return;
        }

        if (Config.SyncIntervalMinutes <= 0)
        {
            Logger.LogInformation(
                "{ConnectorName} connector is disabled (SyncIntervalMinutes <= 0), background service will not run",
                ConnectorName
            );
            return;
        }

        Logger.LogInformation(
            "{ConnectorName} connector background service started with {SyncInterval} minute intervals",
            ConnectorName,
            Config.SyncIntervalMinutes
        );

        try
        {
            var syncInterval = TimeSpan.FromMinutes(Config.SyncIntervalMinutes);

            using var timer = new PeriodicTimer(syncInterval);

            do
            {
                try
                {
                    Logger.LogDebug("Starting {ConnectorName} data sync cycle", ConnectorName);

                    var success = await PerformSyncAsync(stoppingToken);

                    if (success)
                    {
                        Logger.LogInformation(
                            "{ConnectorName} data sync completed successfully",
                            ConnectorName
                        );
                    }
                    else
                    {
                        Logger.LogWarning("{ConnectorName} data sync failed", ConnectorName);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error during {ConnectorName} data sync cycle", ConnectorName);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation(
                "{ConnectorName} connector background service cancellation requested",
                ConnectorName
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Unexpected error in {ConnectorName} connector background service",
                ConnectorName
            );
            throw;
        }
        finally
        {
            Logger.LogInformation(
                "{ConnectorName} connector background service stopped",
                ConnectorName
            );
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation(
            "{ConnectorName} connector background service is stopping...",
            ConnectorName
        );
        await base.StopAsync(cancellationToken);
    }
}
