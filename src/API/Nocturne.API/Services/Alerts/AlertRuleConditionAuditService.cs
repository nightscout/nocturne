using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Startup scan that logs a warning for every enabled alert rule with a condition tree that cannot
/// be evaluated (<see cref="IAlertRuleConditionValidator"/>), such as one stored before saves
/// checked it. The log names the rule and each problem's path and reason code, so an operator can
/// find and fix it. Changes nothing.
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
            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            var validator = scope.ServiceProvider.GetRequiredService<IAlertRuleConditionValidator>();

            var tenantIds = await scope.ServiceProvider.GetRequiredService<ITenantRepository>()
                .ListIdsAsync(activeOnly: true, stoppingToken);

            var flagged = 0;
            foreach (var tenantId in tenantIds)
            {
                try
                {
                    flagged += await AuditTenantAsync(alerts, validator, tenantId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error auditing alert rule conditions for tenant {TenantId}", tenantId);
                }
            }

            if (flagged > 0)
            {
                _logger.LogWarning(
                    "{Count} enabled alert rule(s) have conditions that cannot be evaluated", flagged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error auditing alert rule conditions");
        }
    }

    private async Task<int> AuditTenantAsync(
        IAlertRepository alerts,
        IAlertRuleConditionValidator validator,
        Guid tenantId,
        CancellationToken ct)
    {
        var (rules, unreadable) = await alerts.ReadEnabledRulesAsync(tenantId, ct);

        // The repository has already logged each unreadable rule by id.
        var flagged = unreadable.Count;
        foreach (var rule in rules)
        {
            IReadOnlyList<RustValidationIssue> issues;
            try
            {
                issues = validator.Validate(
                    rule.ConditionType,
                    rule.ConditionParams,
                    rule.AutoResolveEnabled,
                    rule.AutoResolveParams,
                    rule.ClientConfiguration);
            }
            catch (RustAlertEngineException ex)
            {
                flagged++;
                _logger.LogError(ex,
                    "The alert engine could not check the conditions of alert rule {AlertRuleId} for tenant {TenantId}",
                    rule.Id, tenantId);
                continue;
            }
            if (issues.Count == 0)
                continue;

            flagged++;
            _logger.LogWarning(
                "Alert rule {AlertRuleId} for tenant {TenantId} has conditions that cannot be evaluated: {Issues}",
                rule.Id,
                tenantId,
                string.Join("; ", issues.Select(i => $"{i.Scope} {i.Path}: {i.Reason}")));
        }
        return flagged;
    }
}
