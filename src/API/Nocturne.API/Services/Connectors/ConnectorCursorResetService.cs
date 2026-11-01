using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Resets connector sync cursors for an arbitrary tenant, fanning out across every connector
/// that tenant has configured. Intended for platform-admin use after an upstream connector fix,
/// when corrected/missing historical data must be re-pushed to affected tenants.
/// </summary>
/// <remarks>
/// Nocturne does not persist a sync cursor — each data type resumes from the latest record already
/// stored. A "cursor reset" is therefore an explicit-range sync with an upper bound of "now", which
/// bypasses the per-type catch-up cursors and forces a genuine re-pull of history. Re-ingested
/// records dedupe on their idempotency keys, so re-running is safe.
/// </remarks>
/// <seealso cref="IConnectorSyncService"/>
public interface IConnectorCursorResetService
{
    /// <summary>
    /// Resets the cursor for every configured connector belonging to <paramref name="tenantId"/>,
    /// in a scope already opened on that tenant (<c>TenantJobScope.Open</c>).
    /// </summary>
    /// <param name="tenantId">The target tenant whose connectors should be re-pulled.</param>
    /// <param name="from">Optional lower bound. When null, all available history is re-ingested.</param>
    /// <param name="dataTypes">Optional data-type filter. When null/empty, every supported type is reset.</param>
    /// <param name="progress">
    /// Optional sink notified as each connector starts and completes, so a long-running fan-out can
    /// report incremental per-connector progress (e.g. to a background-job status endpoint).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A per-connector result, or null when the target tenant does not exist.
    /// </returns>
    Task<TenantCursorResetResult?> ResetTenantCursorsAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        IConnectorResetProgress? progress,
        CancellationToken ct);

    /// <summary>
    /// Lists the connectors configured for <paramref name="tenantId"/> with their last-sync and
    /// health state, so an admin can see the data gap before/after a reset.
    /// </summary>
    /// <param name="tenantId">The target tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The configured connectors, or null when the target tenant does not exist.</returns>
    Task<TenantConnectorsDto?> GetTenantConnectorsAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Receives incremental per-connector progress while a tenant-wide cursor reset runs. Used by the
/// background-job wrapper (<see cref="IConnectorCursorResetJobService"/>) to surface progress to a
/// polling client; synchronous callers pass <see langword="null"/>.
/// </summary>
public interface IConnectorResetProgress
{
    /// <summary>Called immediately before a connector's re-pull begins.</summary>
    /// <param name="connectorName">The connector about to be reset (e.g. <c>nightscout</c>).</param>
    void ConnectorStarted(string connectorName);

    /// <summary>Called once a connector's re-pull has finished (whether it succeeded or failed).</summary>
    /// <param name="result">The per-connector outcome.</param>
    void ConnectorCompleted(ConnectorCursorResetResult result);
}

/// <summary>Sync/health summary for a single configured connector.</summary>
/// <param name="ConnectorName">The connector name (e.g. <c>nightscout</c>).</param>
/// <param name="IsHealthy">Whether the connector last reported healthy.</param>
/// <param name="LastSuccessfulSync">When the connector last completed a successful sync.</param>
/// <param name="LastSyncAttempt">When the connector last attempted a sync.</param>
/// <param name="LastErrorMessage">The most recent error message, if any.</param>
public record TenantConnectorSummary(
    string ConnectorName,
    bool IsHealthy,
    DateTime? LastSuccessfulSync,
    DateTime? LastSyncAttempt,
    string? LastErrorMessage);

/// <summary>A tenant and the connectors it has configured.</summary>
/// <param name="TenantId">The tenant id.</param>
/// <param name="TenantSlug">The tenant slug, for display.</param>
/// <param name="Connectors">One entry per configured connector.</param>
public record TenantConnectorsDto(
    Guid TenantId,
    string TenantSlug,
    IReadOnlyList<TenantConnectorSummary> Connectors);

/// <summary>Per-connector outcome of a tenant-wide cursor reset.</summary>
/// <param name="ConnectorName">The connector that was reset (e.g. <c>nightscout</c>).</param>
/// <param name="Result">The sync result returned by the connector executor.</param>
public record ConnectorCursorResetResult(string ConnectorName, SyncResult Result);

/// <summary>Aggregate result of resetting every connector for a single tenant.</summary>
/// <param name="TenantId">The tenant that was processed.</param>
/// <param name="TenantSlug">The tenant's slug, for display.</param>
/// <param name="Connectors">One entry per configured connector.</param>
public record TenantCursorResetResult(
    Guid TenantId,
    string TenantSlug,
    IReadOnlyList<ConnectorCursorResetResult> Connectors);

/// <inheritdoc cref="IConnectorCursorResetService"/>
public class ConnectorCursorResetService : IConnectorCursorResetService
{
    private readonly IConnectorConfigurationRepository _configurations;
    private readonly ITenantRepository _tenants;
    private readonly IConnectorSyncService _syncService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectorCursorResetService> _logger;

    /// <summary>Initializes a new instance of <see cref="ConnectorCursorResetService"/>.</summary>
    public ConnectorCursorResetService(
        IConnectorConfigurationRepository configurations,
        ITenantRepository tenants,
        IConnectorSyncService syncService,
        ITenantAccessor tenantAccessor,
        TimeProvider timeProvider,
        ILogger<ConnectorCursorResetService> logger)
    {
        _configurations = configurations;
        _tenants = tenants;
        _syncService = syncService;
        _tenantAccessor = tenantAccessor;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantCursorResetResult?> ResetTenantCursorsAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        IConnectorResetProgress? progress,
        CancellationToken ct)
    {
        var tenant = await _tenants.GetAsync(tenantId, ct);

        if (tenant is null)
        {
            _logger.LogWarning("Cursor reset requested for unknown tenant {TenantId}", tenantId);
            return null;
        }

        // The configuration read and the executor scope IConnectorSyncService.TriggerSyncAsync opens
        // both read the tenant from the accessor, so the caller opens this scope on the target tenant
        // (TenantJobScope.Open); moving the accessor here would move every context of the scope.
        if (_tenantAccessor.Context?.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"A cursor reset for tenant {tenantId} must run in a scope opened on that tenant.");
        }

        var configs = await _configurations.ListAsync(ct);

        var results = new List<ConnectorCursorResetResult>(configs.Count);

        // Loop-invariant: setting To forces "explicit range" mode in the connectors, bypassing the
        // per-type catch-up cursors so history is genuinely re-pulled. A null From means no lower bound.
        var request = new SyncRequest
        {
            From = from,
            To = _time.GetUtcNow().UtcDateTime,
            DataTypes = dataTypes ?? [],
        };

        foreach (var config in configs)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Resetting cursor for connector {ConnectorName} in tenant {TenantSlug} (from {From})",
                config.ConnectorName, tenant.Slug, from?.ToString("o") ?? "beginning");

            progress?.ConnectorStarted(config.ConnectorName);

            // Isolate each connector: one failing must not abort the rest of the tenant's fan-out,
            // and the response should carry one entry per configured connector. Cancellation still
            // propagates so a cancelled request stops cleanly.
            ConnectorCursorResetResult outcome;
            try
            {
                var result = await _syncService.TriggerSyncAsync(config.ConnectorName, request, ct);
                outcome = new ConnectorCursorResetResult(config.ConnectorName, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Cursor reset failed for connector {ConnectorName} in tenant {TenantSlug}",
                    config.ConnectorName, tenant.Slug);
                outcome = new ConnectorCursorResetResult(
                    config.ConnectorName,
                    new SyncResult { Success = false, Message = ex.Message });
            }

            results.Add(outcome);
            progress?.ConnectorCompleted(outcome);
        }

        _logger.LogInformation(
            "Cursor reset complete for tenant {TenantSlug}: {ConnectorCount} connectors processed",
            tenant.Slug, results.Count);

        return new TenantCursorResetResult(tenant.Id, tenant.Slug, results);
    }

    /// <inheritdoc />
    public async Task<TenantConnectorsDto?> GetTenantConnectorsAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _tenants.GetAsync(tenantId, ct);

        if (tenant is null)
            return null;

        // The job service asks before it has opened the target tenant's scope.
        var connectors = (await _configurations.ListForTenantAsync(tenantId, ct))
            .Select(c => new TenantConnectorSummary(
                c.ConnectorName,
                c.IsHealthy,
                c.LastSuccessfulSync,
                c.LastSyncAttempt,
                c.LastErrorMessage))
            .ToList();

        return new TenantConnectorsDto(tenant.Id, tenant.Slug, connectors);
    }

}
