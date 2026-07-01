using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Alerts.Native;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// One-time startup backfill that stamps <c>scope_class</c> on every existing alert rule for
/// scoped Do Not Disturb (ADR 0004). Pre-existing rules were created before classification
/// existed and default to <c>undirected</c> (all-only) from the D2 migration; this recomputes
/// each one through <see cref="IRuleScopeClassifier"/> so scoped <c>lows</c>/<c>highs</c> windows
/// can narrow-match them. After this lands, the controller computes <c>scope_class</c> on every
/// create/update, so the steady state is a no-op.
/// </summary>
/// <remarks>
/// Idempotent and safe to run on every startup: only rows whose recomputed class differs from the
/// stored one are written, so once the population is classified the scan writes nothing. The scan
/// is per-tenant because <c>alert_rules</c> is RLS-scoped and the policy is fail-closed — a
/// cross-tenant <c>IgnoreQueryFilters</c> read would be blocked, so we set the tenant context per
/// iteration exactly like the sweep service does.
/// </remarks>
/// <seealso cref="RuleScopeClassifier"/>
public sealed class RuleScopeClassBackfillService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RuleScopeClassBackfillService> _logger;

    public RuleScopeClassBackfillService(
        IServiceProvider serviceProvider,
        ILogger<RuleScopeClassBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Without the native engine every Classify falls back to Undirected, and the
            // recompute-and-compare below would overwrite previously-correct low/high
            // classifications with that fallback. Skip the whole scan instead of persisting it.
            if (!AlertsInterop.IsAvailable())
            {
                _logger.LogWarning(
                    "nocturne_alerts native library unavailable; skipping scope-class backfill to preserve stored classifications");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var classifier = scope.ServiceProvider.GetRequiredService<IRuleScopeClassifier>();

            // Tenants are not RLS-scoped, so this list read is safe without a tenant context.
            List<Guid> tenantIds;
            await using (var lookup = await factory.CreateDbContextAsync(cancellationToken))
            {
                tenantIds = await lookup.Tenants
                    .AsNoTracking()
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync(cancellationToken);
            }

            var updatedTotal = 0;
            foreach (var tenantId in tenantIds)
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                db.TenantId = tenantId;

                // Tracked (not AsNoTracking) so reclassified rows are persisted by SaveChanges.
                var rules = await db.AlertRules
                    .Where(r => r.TenantId == tenantId)
                    .ToListAsync(cancellationToken);

                var updated = 0;
                foreach (var rule in rules)
                {
                    var computed = classifier.Classify(rule.ConditionType, rule.ConditionParams);
                    if (rule.ScopeClass != computed)
                    {
                        rule.ScopeClass = computed;
                        updated++;
                    }
                }

                if (updated > 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                    updatedTotal += updated;
                }
            }

            if (updatedTotal > 0)
            {
                _logger.LogInformation(
                    "Backfilled scope_class for {Count} alert rule(s) across {Tenants} tenant(s)",
                    updatedTotal,
                    tenantIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backfilling alert-rule scope classes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
