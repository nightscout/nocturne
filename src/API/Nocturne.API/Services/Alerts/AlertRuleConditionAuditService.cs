using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Startup scan that logs a warning for every enabled alert rule whose condition trees a save
/// would now reject (<see cref="IAlertRuleConditionValidator"/>). A rule stored before that check
/// existed may be skipped on every evaluation or never able to fire. The log names the rule and
/// each problem's path and reason code, so an operator can find and fix it. Changes nothing.
/// </summary>
/// <remarks>
/// Per tenant because <c>alert_rules</c> is RLS-scoped and fail-closed; see
/// <see cref="RuleScopeClassBackfillService"/>, which scans the same way.
/// </remarks>
public sealed class AlertRuleConditionAuditService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AlertRuleConditionAuditService> _logger;

    public AlertRuleConditionAuditService(
        IServiceProvider serviceProvider,
        ILogger<AlertRuleConditionAuditService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var validator = scope.ServiceProvider.GetRequiredService<IAlertRuleConditionValidator>();

            List<Guid> tenantIds;
            await using (var lookup = await factory.CreateDbContextAsync(stoppingToken))
            {
                tenantIds = await lookup.Tenants
                    .AsNoTracking()
                    .Where(t => t.IsActive)
                    .Select(t => t.Id)
                    .ToListAsync(stoppingToken);
            }

            var flagged = 0;
            foreach (var tenantId in tenantIds)
            {
                try
                {
                    flagged += await AuditTenantAsync(factory, validator, tenantId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error auditing alert rule conditions for tenant {TenantId}", tenantId);
                }
            }

            if (flagged > 0)
            {
                _logger.LogWarning(
                    "{Count} enabled alert rule(s) have conditions a save would reject", flagged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error auditing alert rule conditions");
        }
    }

    private async Task<int> AuditTenantAsync(
        IDbContextFactory<NocturneDbContext> factory,
        IAlertRuleConditionValidator validator,
        Guid tenantId,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var rules = await db.AlertRules
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.IsEnabled)
            .Select(r => new
            {
                r.Id,
                r.ConditionType,
                r.ConditionParams,
                r.AutoResolveEnabled,
                r.AutoResolveParams,
                r.ClientConfiguration,
            })
            .ToListAsync(ct);

        var flagged = 0;
        foreach (var rule in rules)
        {
            var issues = validator.Validate(
                rule.ConditionType,
                rule.ConditionParams,
                rule.AutoResolveEnabled,
                rule.AutoResolveParams,
                rule.ClientConfiguration);
            if (issues.Count == 0)
                continue;

            flagged++;
            _logger.LogWarning(
                "Alert rule {AlertRuleId} for tenant {TenantId} has conditions a save would reject: {Issues}",
                rule.Id,
                tenantId,
                string.Join("; ", issues.Select(i => $"{i.Scope} {i.Path}: {i.Reason}")));
        }
        return flagged;
    }
}
