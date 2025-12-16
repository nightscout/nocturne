using Nocturne.Core.Models.Services;

namespace Nocturne.API.Services;

/// <summary>
/// Service for manually triggering data synchronization across all enabled connectors
/// </summary>
public interface IManualSyncService
{
    /// <summary>
    /// Triggers a manual sync of all enabled connectors for the specified lookback period
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Results of the manual sync operation</returns>
    Task<ManualSyncResult> TriggerManualSyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether manual sync is configured and available
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// Checks if there are any connectors configured and enabled
    /// </summary>
    bool HasEnabledConnectors();

    /// <summary>
    /// Checks if a specific connector is configured and enabled
    /// </summary>
    /// <param name="connectorId">The connector ID (e.g., "dexcom", "glooko")</param>
    bool IsConnectorConfigured(string connectorId);
}
