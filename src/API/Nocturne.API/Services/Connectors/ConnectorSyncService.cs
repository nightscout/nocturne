using Nocturne.Core.Contracts.Connectors;
using Nocturne.API.Helpers;
using Nocturne.API.Multitenancy;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Service for triggering manual on-demand sync operations for data source connectors.
/// </summary>
/// <seealso cref="ConnectorSyncService"/>
public interface IConnectorSyncService
{
    /// <summary>
    /// Triggers an immediate sync for the specified connector within the current tenant context.
    /// </summary>
    /// <param name="connectorId">The connector identifier (e.g., <c>"dexcom"</c>, <c>"nightscout"</c>).</param>
    /// <param name="request">The sync request parameters such as date range and data types.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SyncResult"/> indicating success or failure with a status message.</returns>
    Task<SyncResult> TriggerSyncAsync(
        string connectorId,
        SyncRequest request,
        CancellationToken ct
    );
}

/// <summary>
/// Implementation of <see cref="IConnectorSyncService"/> that resolves the appropriate
/// <see cref="IConnectorSyncExecutor"/> from a child DI scope and executes the sync
/// with the current tenant context propagated into that scope.
/// </summary>
/// <remarks>
/// A child scope is created per sync operation so that scoped services (e.g., DbContext,
/// tenant-aware repositories) are properly isolated and disposed after the sync completes.
/// </remarks>
/// <seealso cref="IConnectorSyncService"/>
/// <seealso cref="ISyncProgressReporter"/>
public class ConnectorSyncService : IConnectorSyncService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<ConnectorSyncService> _logger;
    private readonly ISyncProgressReporter _progressReporter;
    private readonly TenantRunGuard _runGuard;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectorSyncService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root service provider used to create child scopes per sync.</param>
    /// <param name="tenantAccessor">Provides the current tenant context to propagate into each sync scope.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="progressReporter">Reporter for streaming sync progress events to clients via SignalR.</param>
    /// <param name="runGuard">Refuses a sync for a connector and tenant that is already running.</param>
    /// <param name="timeProvider">Clock the run's <see cref="SyncResult.DurationMs"/> is measured on.</param>
    public ConnectorSyncService(
        IServiceProvider serviceProvider,
        ITenantAccessor tenantAccessor,
        ILogger<ConnectorSyncService> logger,
        ISyncProgressReporter progressReporter,
        TenantRunGuard runGuard,
        TimeProvider timeProvider
    )
    {
        _serviceProvider = serviceProvider;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
        _progressReporter = progressReporter;
        _runGuard = runGuard;
        _timeProvider = timeProvider;
    }

    public async Task<SyncResult> TriggerSyncAsync(
        string connectorId,
        SyncRequest request,
        CancellationToken ct
    )
    {
        var loggedConnectorId = LogText.Sanitize(connectorId);

        if (request.WindowError() is { } windowError)
        {
            _logger.LogWarning(
                "Refused manual sync for connector {ConnectorId}: {WindowError}", loggedConnectorId, windowError);
            return new SyncResult { Success = false, Message = windowError, Errors = { windowError } };
        }

        var tenantId = _tenantAccessor.Context?.TenantId ?? Guid.Empty;
        using var lease = _runGuard.TryAcquire(tenantId, connectorId);
        if (lease is null)
        {
            _logger.LogInformation(
                "Refused manual sync for connector {ConnectorId}: a sync for this connector is already running",
                loggedConnectorId);
            return new SyncResult
            {
                Success = false,
                AlreadyRunning = true,
                Message = $"A sync for connector '{connectorId}' is already running",
            };
        }

        _logger.LogInformation("Manual sync triggered for connector {ConnectorId}", loggedConnectorId);

        var started = _timeProvider.GetTimestamp();

        try
        {
            // A manual sync is attributed as connector:<id>; a scheduled one runs as job:connector:<id>.
            await using var scope = TenantJobScope.Open(
                _serviceProvider, _tenantAccessor.Context, $"connector:{ConnectorNames.Canonical(connectorId)}");

            var executors = scope.ServiceProvider.GetServices<IConnectorSyncExecutor>();
            var executor = executors.FirstOrDefault(e =>
                e.ConnectorId.Equals(connectorId, StringComparison.OrdinalIgnoreCase));

            if (executor is null)
            {
                _logger.LogWarning(
                    "Unknown or disabled connector {ConnectorId}", loggedConnectorId);
                return new SyncResult
                {
                    Success = false,
                    Message = $"Unknown connector: {connectorId}",
                };
            }

            var result = await executor.ExecuteSyncAsync(scope.ServiceProvider, request, ct, _progressReporter);
            result.DurationMs = ElapsedMs(started);

            _logger.LogInformation(
                "Manual sync for {ConnectorId} completed: Success={Success}, Message={Message}",
                loggedConnectorId,
                result.Success,
                result.Message
            );

            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No service for type"))
        {
            _logger.LogWarning(
                "Connector {ConnectorId} is not registered (likely disabled)",
                loggedConnectorId
            );
            return new SyncResult
            {
                Success = false,
                Message = $"Connector '{connectorId}' is not configured or is disabled",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(
                ex,
                "Error during manual sync for connector {ConnectorId}",
                loggedConnectorId
            );
            return new SyncResult
            {
                Success = false,
                Message = $"Sync failed: {ex.Message}",
                DurationMs = ElapsedMs(started),
            };
        }
    }

    private long ElapsedMs(long started) =>
        (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
}
