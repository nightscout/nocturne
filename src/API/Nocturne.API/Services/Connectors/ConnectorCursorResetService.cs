using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

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
    /// Resets the cursor for every configured connector belonging to <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">The target tenant whose connectors should be re-pulled.</param>
    /// <param name="from">Optional lower bound. When null, all available history is re-ingested.</param>
    /// <param name="dataTypes">Optional data-type filter. When null/empty, every supported type is reset.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A per-connector result, or null when the target tenant does not exist.
    /// </returns>
    Task<TenantCursorResetResult?> ResetTenantCursorsAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
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
    private readonly NocturneDbContext _db;
    private readonly IConnectorSyncService _syncService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<ConnectorCursorResetService> _logger;

    /// <summary>Initializes a new instance of <see cref="ConnectorCursorResetService"/>.</summary>
    public ConnectorCursorResetService(
        NocturneDbContext db,
        IConnectorSyncService syncService,
        ITenantAccessor tenantAccessor,
        ILogger<ConnectorCursorResetService> logger)
    {
        _db = db;
        _syncService = syncService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TenantCursorResetResult?> ResetTenantCursorsAsync(
        Guid tenantId,
        DateTime? from,
        List<SyncDataType>? dataTypes,
        CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null)
        {
            _logger.LogWarning("Cursor reset requested for unknown tenant {TenantId}", tenantId);
            return null;
        }

        // Switch the ambient tenant context to the target tenant so that RLS-scoped queries and the
        // sync executors operate under the right tenant. This is the same mechanism the dev-only
        // sync-all endpoint and the migration background jobs use.
        await SetTenantGucAsync(tenantId, ct);
        _tenantAccessor.SetTenant(new TenantContext(
            tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo));

        var configs = await _db.ConnectorConfigurations.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.ConnectorName)
            .ToListAsync(ct);

        var results = new List<ConnectorCursorResetResult>(configs.Count);

        foreach (var config in configs)
        {
            ct.ThrowIfCancellationRequested();

            // Setting To forces "explicit range" mode in the connectors, bypassing the per-type
            // catch-up cursors so history is genuinely re-pulled. A null From means no lower bound.
            var request = new SyncRequest
            {
                From = from,
                To = DateTime.UtcNow,
                DataTypes = dataTypes ?? [],
            };

            _logger.LogInformation(
                "Resetting cursor for connector {ConnectorName} in tenant {TenantSlug} (from {From})",
                config.ConnectorName, tenant.Slug, from?.ToString("o") ?? "beginning");

            var result = await _syncService.TriggerSyncAsync(config.ConnectorName, request, ct);
            results.Add(new ConnectorCursorResetResult(config.ConnectorName, result));
        }

        _logger.LogInformation(
            "Cursor reset complete for tenant {TenantSlug}: {ConnectorCount} connectors processed",
            tenant.Slug, results.Count);

        return new TenantCursorResetResult(tenant.Id, tenant.Slug, results);
    }

    /// <inheritdoc />
    public async Task<TenantConnectorsDto?> GetTenantConnectorsAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null)
            return null;

        await SetTenantGucAsync(tenantId, ct);

        var connectors = await _db.ConnectorConfigurations.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.ConnectorName)
            .Select(c => new TenantConnectorSummary(
                c.ConnectorName,
                c.IsHealthy,
                c.LastSuccessfulSync,
                c.LastSyncAttempt,
                c.LastErrorMessage))
            .ToListAsync(ct);

        return new TenantConnectorsDto(tenant.Id, tenant.Slug, connectors);
    }

    /// <summary>
    /// Sets the PostgreSQL RLS GUC for the target tenant. No-op on non-relational providers
    /// (e.g. the EF Core in-memory provider used by unit tests), where RLS does not apply.
    /// </summary>
    private async Task SetTenantGucAsync(Guid tenantId, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return;

        await _db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant_id', {0}, false)",
            [tenantId.ToString()],
            ct);
    }
}
