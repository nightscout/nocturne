using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    Logger.LogError(ex, "Error during {ConnectorName} data sync cycle {ex}", ConnectorName, ex);
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
                "Unexpected error in {ConnectorName} connector background service {ex}",
                ConnectorName,
                ex
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
