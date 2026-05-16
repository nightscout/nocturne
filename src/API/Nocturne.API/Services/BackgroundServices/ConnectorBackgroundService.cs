using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Abstract base class for connector background services that poll external data sources
/// on a per-tenant basis within the API process.
/// </summary>
/// <typeparam name="TConfig">
/// The connector configuration type, which must extend <see cref="BaseConnectorConfiguration"/>.
/// </typeparam>
/// <remarks>
/// The service polls every minute and only syncs a given tenant when its configured
/// <c>SyncIntervalMinutes</c> has elapsed since the last sync. Per-tenant configuration
/// is loaded fresh each cycle via <see cref="IConnectorConfigurationLoader{TConfig}"/>.
/// </remarks>
public abstract class ConnectorBackgroundService<TConfig> : BackgroundService
    where TConfig : BaseConnectorConfiguration
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly ILogger Logger;

    /// <summary>
    /// Tracks the last sync time per tenant so each tenant's configured
    /// SyncIntervalMinutes is respected independently.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastSyncByTenant = new();

    /// <summary>
    /// Initialises a new <see cref="ConnectorBackgroundService{TConfig}"/>.
    /// </summary>
    /// <param name="serviceProvider">Root DI service provider; a new scope is created per tenant sync.</param>
    /// <param name="logger">Logger instance.</param>
    protected ConnectorBackgroundService(
        IServiceProvider serviceProvider,
        ILogger logger
    )
    {
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the connector name for logging
    /// </summary>
    protected abstract string ConnectorName { get; }

    /// <summary>
    /// Performs a single sync operation using the connector service.
    /// Services should be resolved from the provided <paramref name="scopeProvider"/>
    /// which has the tenant context already set.
    /// </summary>
    /// <param name="scopeProvider">Tenant-scoped service provider</param>
    /// <param name="config">Per-tenant connector configuration loaded by the framework</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="progressReporter">Optional progress reporter for sync status updates</param>
    /// <returns>A SyncResult indicating success/failure and any error details</returns>
    protected abstract Task<SyncResult> PerformSyncAsync(
        IServiceProvider scopeProvider,
        TConfig config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null);

    /// <summary>
    /// Persists the health state for this connector to the database via <see cref="IConnectorConfigurationService"/>.
    /// Errors are swallowed and logged as warnings so that health-state failures do not abort sync.
    /// </summary>
    private async Task UpdateHealthStateAsync(
        IServiceProvider scopeProvider,
        DateTime? lastSyncAttempt = null,
        DateTime? lastSuccessfulSync = null,
        string? lastErrorMessage = null,
        DateTime? lastErrorAt = null,
        bool? isHealthy = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var configService = scopeProvider.GetRequiredService<IConnectorConfigurationService>();

            await configService.UpdateHealthStateAsync(
                ConnectorName,
                lastSyncAttempt,
                lastSuccessfulSync,
                lastErrorMessage,
                lastErrorAt,
                isHealthy,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to update health state for {ConnectorName}",
                ConnectorName
            );
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly to let the application fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        Logger.LogInformation(
            "{ConnectorName} connector background service started",
            ConnectorName);

        try
        {
            // Poll every minute; each tenant is only synced when its own
            // SyncIntervalMinutes has elapsed since its last sync.
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

            do
            {
                try
                {
                    await SyncAllTenantsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogError(ex, "Error during {ConnectorName} tenant sync cycle", ConnectorName);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("{ConnectorName} connector background service stopping", ConnectorName);
        }
        finally
        {
            Logger.LogInformation(
                "{ConnectorName} connector background service stopped",
                ConnectorName);
        }
    }

    private async Task SyncAllTenantsAsync(CancellationToken stoppingToken)
    {
        using var lookupScope = ServiceProvider.CreateScope();
        var factory = lookupScope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var lookupContext = await factory.CreateDbContextAsync(stoppingToken);
        var tenants = await lookupContext.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Slug, t.DisplayName })
            .ToListAsync(stoppingToken);

        foreach (var tenant in tenants)
        {
            try
            {
                await SyncForTenantAsync(tenant.Id, tenant.Slug, tenant.DisplayName, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex,
                    "Error syncing {ConnectorName} for tenant {TenantSlug}",
                    ConnectorName, tenant.Slug);
            }
        }
    }

    private async Task SyncForTenantAsync(Guid tenantId, string tenantSlug, string displayName, CancellationToken stoppingToken)
    {
        using var scope = ServiceProvider.CreateScope();

        // Set tenant context for this scope
        var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        tenantAccessor.SetTenant(new TenantContext(tenantId, tenantSlug, displayName, true));

        // Populate audit context so mutations are attributed to this connector
        var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        dbContext.AuditContext = SystemAuditContext.ForService($"connector:{ConnectorName}");

        // Load per-tenant config via the loader
        var loader = scope.ServiceProvider.GetRequiredService<IConnectorConfigurationLoader<TConfig>>();
        TConfig config;
        try
        {
            config = await loader.LoadForTenantAsync(scope.ServiceProvider, stoppingToken);
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "Failed to load config for {ConnectorName}/{TenantSlug}", ConnectorName, tenantSlug);
            return;
        }
        catch (DbUpdateException ex)
        {
            Logger.LogWarning(ex, "Failed to load config for {ConnectorName}/{TenantSlug}", ConnectorName, tenantSlug);
            return;
        }

        if (!config.Enabled || config.SyncIntervalMinutes <= 0)
            return;

        // Only sync when the tenant's configured interval has elapsed
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromMinutes(config.SyncIntervalMinutes);
        if (_lastSyncByTenant.TryGetValue(tenantId, out var lastSync) && now - lastSync < interval)
            return;

        Logger.LogDebug("Syncing {ConnectorName} for tenant {TenantSlug}", ConnectorName, tenantSlug);

        _lastSyncByTenant[tenantId] = now;

        await UpdateHealthStateAsync(
            scope.ServiceProvider,
            lastSyncAttempt: now,
            cancellationToken: stoppingToken);

        var progressReporter = scope.ServiceProvider.GetService<ISyncProgressReporter>();
        var result = await PerformSyncAsync(scope.ServiceProvider, config, stoppingToken, progressReporter);

        if (result.Success)
        {
            Logger.LogInformation(
                "{ConnectorName} sync completed for tenant {TenantSlug}",
                ConnectorName, tenantSlug);

            await UpdateHealthStateAsync(
                scope.ServiceProvider,
                lastSuccessfulSync: DateTime.UtcNow,
                isHealthy: true,
                lastErrorMessage: string.Empty,
                lastErrorAt: DateTime.MinValue,
                cancellationToken: stoppingToken);
        }
        else
        {
            var errorMessage = result.Errors.Count > 0
                ? string.Join("; ", result.Errors)
                : !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : "Sync failed";

            Logger.LogWarning(
                "{ConnectorName} sync failed for tenant {TenantSlug}: {ErrorMessage}",
                ConnectorName, tenantSlug, errorMessage);

            await UpdateHealthStateAsync(
                scope.ServiceProvider,
                isHealthy: false,
                lastErrorMessage: errorMessage,
                lastErrorAt: DateTime.UtcNow,
                cancellationToken: stoppingToken);
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
