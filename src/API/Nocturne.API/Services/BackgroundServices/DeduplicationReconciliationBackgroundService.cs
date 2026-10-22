using Nocturne.API.Multitenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Periodically drives cursor-based deduplication reconciliation for every active tenant.
/// On each tick it enumerates active tenants and calls
/// <see cref="IDeduplicationService.ReconcileNewLinksAsync(int, int, CancellationToken)"/> for each,
/// merging duplicate canonical groups created since the tenant's persisted cursor, then
/// <see cref="IDeduplicationService.RepairStateSpanPrimariesAsync(CancellationToken)"/> and
/// <see cref="IDeduplicationService.PruneSingletonLinksAsync(int, int, CancellationToken)"/>. A tenant
/// whose merge lock is held has its merges left to the next tick.
/// </summary>
internal sealed class DeduplicationReconciliationBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly int BatchSize = 5000;
    private static readonly int MaxBatchesPerTick = 4;
    private static readonly int PruneBatchSize = 10_000;
    private static readonly int MaxPruneBatchesPerTick = 10;

    private const string AuditEndpoint = "service:dedup-reconciliation";

    private readonly IServiceProvider _serviceProvider;
    private readonly ActiveTenantSnapshot _activeTenants;
    private readonly ILogger<DeduplicationReconciliationBackgroundService> _logger;

    /// <summary>
    /// Initialises a new <see cref="DeduplicationReconciliationBackgroundService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root DI service provider; a new scope is created per tenant.</param>
    /// <param name="activeTenants">The active tenants to reconcile.</param>
    /// <param name="logger">Logger instance.</param>
    public DeduplicationReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        ActiveTenantSnapshot activeTenants,
        ILogger<DeduplicationReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _activeTenants = activeTenants ?? throw new ArgumentNullException(nameof(activeTenants));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly to let the application fully start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        _logger.LogInformation("Deduplication reconciliation background service started");

        try
        {
            using var timer = new PeriodicTimer(Interval);

            do
            {
                try
                {
                    await ReconcileAllTenantsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error during deduplication reconciliation cycle");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Deduplication reconciliation background service stopping");
        }
    }

    /// <summary>
    /// Loads active tenants and reconciles each one sequentially. Per-tenant failures are logged
    /// and swallowed so a single tenant's error does not abort the sweep.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ReconcileAllTenantsAsync(CancellationToken ct)
    {
        var tenants = await _activeTenants.GetAsync(ct);

        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = TenantJobScope.Open(
                    _serviceProvider,
                    new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                    AuditEndpoint);

                var dedup = scope.ServiceProvider.GetRequiredService<IDeduplicationService>();
                var result = await dedup.ReconcileNewLinksAsync(BatchSize, MaxBatchesPerTick, ct);
                var repaired = await dedup.RepairStateSpanPrimariesAsync(ct);
                var pruned = await dedup.PruneSingletonLinksAsync(PruneBatchSize, MaxPruneBatchesPerTick, ct);

                _logger.LogDebug(
                    "Reconciled deduplication for tenant {TenantSlug}: {GroupsMerged} groups merged, {SpanPrimariesRepaired} state span primaries repaired, {LinksPruned} singleton links pruned",
                    tenant.Slug, result.GroupsMerged, repaired, pruned);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Error reconciling deduplication for tenant {TenantSlug}",
                    tenant.Slug);
            }
        }
    }
}
