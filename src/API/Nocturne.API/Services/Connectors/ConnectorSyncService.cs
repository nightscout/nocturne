using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;

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

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectorSyncService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root service provider used to create child scopes per sync.</param>
    /// <param name="tenantAccessor">Provides the current tenant context to propagate into each sync scope.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="progressReporter">Reporter for streaming sync progress events to clients via SignalR.</param>
    public ConnectorSyncService(
        IServiceProvider serviceProvider,
        ITenantAccessor tenantAccessor,
        ILogger<ConnectorSyncService> logger,
        ISyncProgressReporter progressReporter
    )
    {
        _serviceProvider = serviceProvider;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
        _progressReporter = progressReporter;
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
            using var scope = _serviceProvider.CreateScope();

            if (_tenantAccessor.Context is { } tenantContext)
            {
                var scopedTenantAccessor =
                    scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                scopedTenantAccessor.SetTenant(tenantContext);
            }

            var executors = scope.ServiceProvider.GetServices<IConnectorSyncExecutor>();
            var executor = executors.FirstOrDefault(e =>
                e.ConnectorId.Equals(connectorId, StringComparison.OrdinalIgnoreCase));

            if (executor is null)
            {
                _logger.LogWarning(
                    "Unknown or disabled connector {ConnectorId}", connectorId);
                return new SyncResult
                {
                    Success = false,
                    Message = $"Unknown connector: {connectorId}",
                };
            }

            // Attributed as ConnectorBackgroundService attributes a scheduled sync.
            using var systemScope = SystemAuditScope.PushForScope(
                scope.ServiceProvider, $"connector:{executor.ConnectorId}");

            var result = await executor.ExecuteSyncAsync(scope.ServiceProvider, request, ct, _progressReporter);

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
}
