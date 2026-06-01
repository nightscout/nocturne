using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Periodically drives watermark-based deduplication reconciliation for every active tenant.
/// On each tick it enumerates active tenants and calls
/// <see cref="IDeduplicationService.ReconcileNewLinksAsync(int, int, CancellationToken)"/> for each,
/// merging duplicate canonical groups created since the tenant's last watermark.
/// </summary>
/// <remarks>
/// Single-instance deployment only. If the API is ever scaled out, gate per-tenant reconcile with a
/// Postgres advisory lock (pg_advisory_xact_lock) so instances don't both reconcile the same tenant.
/// </remarks>
internal sealed class DeduplicationReconciliationBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly int BatchSize = 5000;
    private static readonly int MaxBatchesPerTick = 4;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeduplicationReconciliationBackgroundService> _logger;

    /// <summary>
    /// Initialises a new <see cref="DeduplicationReconciliationBackgroundService"/>.
    /// </summary>
    /// <param name="serviceProvider">Root DI service provider; a new scope is created per tenant.</param>
    /// <param name="logger">Logger instance.</param>
    public DeduplicationReconciliationBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<DeduplicationReconciliationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
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
        using var lookupScope = _serviceProvider.CreateScope();
        var factory = lookupScope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var lookupContext = await factory.CreateDbContextAsync(ct);
        var tenants = await lookupContext.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Select(t => new { t.Id, t.Slug, t.DisplayName })
            .ToListAsync(ct);

        foreach (var tenant in tenants)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();

                var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                tenantAccessor.SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true));

                var dedup = scope.ServiceProvider.GetRequiredService<IDeduplicationService>();
                var result = await dedup.ReconcileNewLinksAsync(BatchSize, MaxBatchesPerTick, ct);

                _logger.LogDebug(
                    "Reconciled deduplication for tenant {TenantSlug}: {GroupsMerged} groups merged",
                    tenant.Slug, result.GroupsMerged);
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
