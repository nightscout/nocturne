using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.MiniMed.Services;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Core.Models;
using Nocturne.Tools.Connect.Configuration;

namespace Nocturne.Tools.Connect.Services;

/// <summary>
/// Service for executing connector synchronization operations
/// </summary>
public class ConnectorExecutionService(
    ILogger<ConnectorExecutionService> logger,
    ILoggerFactory loggerFactory,
    DaemonStatusService? daemonStatusService = null)
{
    private readonly ILogger<ConnectorExecutionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    // Optional dependency

    /// <summary>
    /// Executes the connector synchronization operation
    /// </summary>
    /// <param name="config">Connect configuration</param>
    /// <param name="daemon">Whether to run in daemon mode</param>
    /// <param name="once">Whether to run only once</param>
    /// <param name="interval">Sync interval in minutes for daemon mode</param>
    /// <param name="dryRun">Whether this is a dry run</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> ExecuteConnectorAsync(
        ConnectConfiguration config,
        bool daemon = false,
        bool once = false,
        int interval = 5,
        bool dryRun = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var connectorConfig = CreateConnectorConfiguration(config);
            if (connectorConfig == null)
            {
                _logger.LogError(
                    "Failed to create connector configuration for source: {Source}",
                    config.ConnectSource
                );
                return false;
            }

            using var connector = CreateConnectorService(connectorConfig);
            if (connector == null)
            {
                _logger.LogError(
                    "Failed to create connector service for source: {Source}",
                    config.ConnectSource
                );
                return false;
            }

            _logger.LogInformation(
                "Created {ConnectorType} connector for source: {Source}",
                connector.GetType().Name,
                config.ConnectSource
            );

            // Authenticate with the data source
            _logger.LogInformation("Authenticating with data source...");
            var authResult = await connector.AuthenticateAsync();
            if (!authResult)
            {
                _logger.LogError(
                    "Authentication failed for connector: {Source}",
                    config.ConnectSource
                );
                return false;
            }

            _logger.LogInformation("Authentication successful");

            if (daemon)
            {
                // Register daemon process for status monitoring
                if (daemonStatusService != null)
                {
                    await daemonStatusService.RegisterDaemonAsync(
                        config.ConnectSource,
                        interval,
                        cancellationToken
                    );
                }

                return await RunDaemonModeAsync(
                    connector,
                    connectorConfig,
                    interval,
                    dryRun,
                    cancellationToken
                );
            }
            else
            {
                return await RunOnceModeAsync(
                    connector,
                    connectorConfig,
                    dryRun,
                    cancellationToken
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error executing connector for source: {Source}",
                config.ConnectSource
            );
            return false;
        }
    }

    /// <summary>
    /// Runs the connector in daemon mode with periodic sync
    /// </summary>
    private async Task<bool> RunDaemonModeAsync(
        IConnectorService<IConnectorConfiguration> connector,
        IConnectorConfiguration config,
        int intervalMinutes,
        bool dryRun,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation(
            "Starting daemon mode with {Interval} minute intervals",
            intervalMinutes
        );

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var syncCount = 0;
        var lastSyncTime = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                syncCount++;
                _logger.LogInformation("Starting sync operation #{SyncCount}", syncCount);

                var syncStartTime = DateTime.UtcNow;
                bool syncResult;

                if (dryRun)
                {
                    _logger.LogInformation("Dry run mode - fetching data without uploading");
                    var entries = await connector.FetchGlucoseDataAsync();
                    var entryCount = System.Linq.Enumerable.Count(entries);
                    _logger.LogInformation(
                        "Dry run: Would have uploaded {Count} entries",
                        entryCount
                    );
                    syncResult = true;
                }
                else
                {
                    syncResult = await PerformSyncAsync(connector, config);
                }

                var syncDuration = DateTime.UtcNow - syncStartTime;
                lastSyncTime = DateTime.UtcNow;

                if (syncResult)
                {
                    _logger.LogInformation(
                        "Sync operation #{SyncCount} completed successfully in {Duration:F1}s",
                        syncCount,
                        syncDuration.TotalSeconds
                    );

                    // Update daemon status with successful sync
                    if (daemonStatusService != null)
                    {
                        await daemonStatusService.RecordSyncSuccessAsync(cancellationToken);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Sync operation #{SyncCount} failed after {Duration:F1}s",
                        syncCount,
                        syncDuration.TotalSeconds
                    );

                    // Update daemon status with error
                    if (daemonStatusService != null)
                    {
                        await daemonStatusService.RecordSyncErrorAsync(
                            $"Sync operation #{syncCount} failed",
                            cancellationToken
                        );
                    }
                }

                // Wait for the next interval
                _logger.LogDebug("Next sync in {Interval} minutes", intervalMinutes);

                // Update heartbeat periodically during the wait
                var heartbeatInterval = TimeSpan.FromMinutes(1);
                var elapsed = TimeSpan.Zero;

                while (elapsed < interval && !cancellationToken.IsCancellationRequested)
                {
                    var waitTime =
                        interval - elapsed < heartbeatInterval
                            ? interval - elapsed
                            : heartbeatInterval;
                    await Task.Delay(waitTime, cancellationToken);
                    elapsed += waitTime;

                    // Update heartbeat
                    if (daemonStatusService != null && !cancellationToken.IsCancellationRequested)
                    {
                        await daemonStatusService.UpdateHeartbeatAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "Daemon mode cancelled after {SyncCount} sync operations",
                    syncCount
                );
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in daemon mode sync operation #{SyncCount}", syncCount);

                // Record error in daemon status
                if (daemonStatusService != null)
                {
                    await daemonStatusService.RecordSyncErrorAsync(ex.Message, cancellationToken);
                }

                // Continue daemon mode even after individual sync failures
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }

        _logger.LogInformation(
            "Daemon mode stopped. Completed {SyncCount} sync operations",
            syncCount
        );

        // Clean up daemon status when stopping
        if (daemonStatusService != null)
        {
            await daemonStatusService.RemoveDaemonStatusAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Runs the connector once and exits
    /// </summary>
    private async Task<bool> RunOnceModeAsync(
        IConnectorService<IConnectorConfiguration> connector,
        IConnectorConfiguration config,
        bool dryRun,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation("Running one-time sync operation");

        try
        {
            if (dryRun)
            {
                _logger.LogInformation("Dry run mode - fetching data without uploading");
                var entries = await connector.FetchGlucoseDataAsync();
                var entryCount = System.Linq.Enumerable.Count(entries);
                _logger.LogInformation("Dry run: Would have uploaded {Count} entries", entryCount);
                return true;
            }
            else
            {
                return await PerformSyncAsync(connector, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in one-time sync operation");
            return false;
        }
    }

    /// <summary>
    /// Performs the actual sync operation using the connector's SyncDataAsync method
    /// </summary>
    private async Task<bool> PerformSyncAsync(
        IConnectorService<IConnectorConfiguration> connector,
        IConnectorConfiguration config
    )
    {
        try
        {
            var connectorType = connector.GetType().Name;
            _logger.LogDebug("Performing sync with connector: {ConnectorType}", connectorType);

            // Use the new SyncDataAsync method
            var request = new SyncRequest
            {
                DataTypes = connector.SupportedDataTypes,
                From = DateTime.UtcNow.AddHours(-3), // Default 3-hour lookback
                To = DateTime.UtcNow
            };

            var result = await connector.SyncDataAsync(request, config, CancellationToken.None);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sync operation");
            return false;
        }
    }

    /// <summary>
    /// Creates the appropriate connector configuration based on the source
    /// </summary>
    private IConnectorConfiguration? CreateConnectorConfiguration(ConnectConfiguration config)
    {
        try
        {
            var source = config.ConnectSource?.ToLowerInvariant();

            return source switch
            {
                "glooko" => new GlookoConnectorConfiguration
                {
                    ConnectSource = ConnectSource.Glooko,
                    GlookoUsername = config.GlookoEmail ?? string.Empty,
                    GlookoPassword = config.GlookoPassword ?? string.Empty,
                    GlookoServer = config.GlookoServer,
                    TimezoneOffset = config.GlookoTimezoneOffset,
                },
                "minimedcarelink" or "carelink" => new CareLinkConnectorConfiguration
                {
                    ConnectSource = ConnectSource.CareLink,
                    CareLinkUsername = config.CarelinkUsername ?? string.Empty,
                    CareLinkPassword = config.CarelinkPassword ?? string.Empty,
                    CareLinkCountry = config.CarelinkRegion ?? "US",
                },
                "dexcomshare" or "dexcom" => new DexcomConnectorConfiguration
                {
                    ConnectSource = ConnectSource.Dexcom,
                    DexcomUsername = config.DexcomUsername ?? string.Empty,
                    DexcomPassword = config.DexcomPassword ?? string.Empty,
                    DexcomServer = config.DexcomRegion ?? "US",
                },
                "linkup" or "librelinkup" => new LibreLinkUpConnectorConfiguration
                {
                    ConnectSource = ConnectSource.LibreLinkUp,
                    LibreUsername = config.LibreUsername ?? string.Empty,
                    LibrePassword = config.LibrePassword ?? string.Empty,
                    LibreRegion = config.LibreRegion,
                },
                "nightscout" => new NightscoutConnectorConfiguration
                {
                    ConnectSource = ConnectSource.Nightscout,
                    SourceEndpoint = config.SourceEndpoint ?? string.Empty,
                    SourceApiSecret = config.SourceApiSecret ?? string.Empty,
                },
                "mylife" => new MyLifeConnectorConfiguration
                {
                    ConnectSource = ConnectSource.MyLife,
                    MyLifeUsername = config.MyLifeUsername ?? string.Empty,
                    MyLifePassword = config.MyLifePassword ?? string.Empty,
                    MyLifePatientId = config.MyLifePatientId ?? string.Empty,
                    EnableGlucoseSync = config.MyLifeEnableGlucoseSync,
                    EnableManualBgSync = config.MyLifeEnableManualBgSync,
                    EnableMealCarbConsolidation = config.MyLifeEnableMealCarbConsolidation,
                    EnableTempBasalConsolidation = config.MyLifeEnableTempBasalConsolidation,
                    TempBasalConsolidationWindowMinutes = config.MyLifeTempBasalConsolidationWindowMinutes,
                },
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error creating connector configuration for source: {Source}",
                config.ConnectSource
            );
            return null;
        }
    }

    /// <summary>
    /// Creates the appropriate connector service based on the configuration
    /// </summary>
    private IConnectorService<IConnectorConfiguration>? CreateConnectorService(
        IConnectorConfiguration config
    )
    {
        try
        {
            var source = config.ConnectSource.ToString().ToLowerInvariant();

            return source switch
            {
                "glooko" => CreateGlookoWrapper((GlookoConnectorConfiguration)config),
                "minimedcarelink" or "carelink" => CreateCareLinkWrapper(
                    (CareLinkConnectorConfiguration)config
                ),
                "dexcomshare" or "dexcom" => CreateDexcomWrapper(
                    (DexcomConnectorConfiguration)config
                ),
                "linkup" or "librelinkup" => CreateLibreWrapper(
                    (LibreLinkUpConnectorConfiguration)config
                ),
                "nightscout" => CreateNightscoutWrapper((NightscoutConnectorConfiguration)config),
                "mylife" => CreateMyLifeWrapper((MyLifeConnectorConfiguration)config),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error creating connector service for source: {Source}",
                config.ConnectSource
            );
            return null;
        }
    }

    private IConnectorService<IConnectorConfiguration> CreateGlookoWrapper(
        GlookoConnectorConfiguration config
    )
    {
        var service = new GlookoConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<GlookoConnectorService>(),
            new ProductionRetryDelayStrategy(),
            new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
            null, // IConnectorFileService
            null  // IApiDataSubmitter
        );
        return new ConnectorServiceWrapper<GlookoConnectorConfiguration>(service);
    }

    private IConnectorService<IConnectorConfiguration> CreateCareLinkWrapper(
        CareLinkConnectorConfiguration config
    )
    {
        var service = new CareLinkConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<CareLinkConnectorService>(),
            new ProductionRetryDelayStrategy(),
            new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
            null  // IApiDataSubmitter
        );
        return new ConnectorServiceWrapper<CareLinkConnectorConfiguration>(service);
    }

    private IConnectorService<IConnectorConfiguration> CreateDexcomWrapper(
        DexcomConnectorConfiguration config
    )
    {
        var service = new DexcomConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<DexcomConnectorService>(),
            new ProductionRetryDelayStrategy(),
            new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
            null  // IApiDataSubmitter
        );
        return new ConnectorServiceWrapper<DexcomConnectorConfiguration>(service);
    }

    private IConnectorService<IConnectorConfiguration> CreateLibreWrapper(
        LibreLinkUpConnectorConfiguration config
    )
    {
        var service = new LibreConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<LibreConnectorService>(),
            new ProductionRetryDelayStrategy(),
            new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
            null  // IApiDataSubmitter
        );
        return new ConnectorServiceWrapper<LibreLinkUpConnectorConfiguration>(service);
    }

    private IConnectorService<IConnectorConfiguration> CreateMyLifeWrapper(
        MyLifeConnectorConfiguration config
    )
    {
        var sessionStore = new MyLifeSessionStore();
        var soapClient = new MyLifeSoapClient(
            new HttpClient(),
            _loggerFactory.CreateLogger<MyLifeSoapClient>()
        );
        var tokenProvider = new MyLifeAuthTokenProvider(
            Options.Create(config),
            new HttpClient(),
            soapClient,
            sessionStore,
            _loggerFactory.CreateLogger<MyLifeAuthTokenProvider>()
        );
        var eventsCache = new MyLifeEventsCache(
            sessionStore,
            new MyLifeSyncService(
                soapClient,
                new MyLifeArchiveReader()
            ),
            _loggerFactory.CreateLogger<MyLifeEventsCache>()
        );
        var mapper = new MyLifeEventProcessor();
        var service = new MyLifeConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<MyLifeConnectorService>(),
            tokenProvider,
            eventsCache,
            mapper,
            sessionStore,
            null,
            null,
            null
        );
        return new ConnectorServiceWrapper<MyLifeConnectorConfiguration>(service);
    }

    private IConnectorService<IConnectorConfiguration> CreateNightscoutWrapper(
        NightscoutConnectorConfiguration config
    )
    {
        var service = new NightscoutConnectorService(
            new HttpClient(),
            Options.Create(config),
            _loggerFactory.CreateLogger<NightscoutConnectorService>(),
            new ProductionRetryDelayStrategy(),
            new ProductionRateLimitingStrategy(_loggerFactory.CreateLogger<ProductionRateLimitingStrategy>()),
            null  // IApiDataSubmitter
        );
        return new ConnectorServiceWrapper<NightscoutConnectorConfiguration>(service);
    }
}

/// <summary>
/// Wrapper class to handle the generic variance issue with connector services
/// </summary>
internal class ConnectorServiceWrapper<TConfig> : IConnectorService<IConnectorConfiguration>
    where TConfig : class, IConnectorConfiguration
{
    private readonly IConnectorService<TConfig> _innerService;

    public ConnectorServiceWrapper(IConnectorService<TConfig> innerService)
    {
        _innerService = innerService ?? throw new ArgumentNullException(nameof(innerService));
    }

    public string ServiceName => _innerService.ServiceName;

    public List<SyncDataType> SupportedDataTypes => _innerService.SupportedDataTypes;

    public Task<bool> AuthenticateAsync() => _innerService.AuthenticateAsync();

    public Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null) =>
        _innerService.FetchGlucoseDataAsync(since);

    public Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        IConnectorConfiguration config,
        CancellationToken cancellationToken
    ) => _innerService.SyncDataAsync(request, (TConfig)config, cancellationToken);

    public void Dispose() => _innerService.Dispose();
}
