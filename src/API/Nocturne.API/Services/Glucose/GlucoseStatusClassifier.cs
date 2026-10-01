using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Classifies a tenant's latest glucose reading into a <see cref="GlucoseStatus"/> against
/// thresholds resolved from configuration and the tenant's enabled threshold alert rules.
/// </summary>
public interface IGlucoseStatusClassifier
{
    /// <summary>Configured thresholds, before any tenant rule overrides them.</summary>
    TenantOverviewThresholds Defaults { get; }

    /// <summary>
    /// Resolves the thresholds for the tenant <paramref name="db"/> is pinned to. Share RLS hides
    /// <c>alert_rules</c>, so a public share is classified against <see cref="Defaults"/>.
    /// </summary>
    Task<TenantOverviewThresholds> ResolveThresholdsAsync(NocturneDbContext db, CancellationToken ct);

    /// <summary>
    /// Classifies a reading. <paramref name="lastReadingAt"/> stands in for the reading's time
    /// when the reading itself is not visible, so a tenant can still be reported stale.
    /// </summary>
    GlucoseStatus Classify(
        double? mgdl,
        DateTime? readingTimestamp,
        DateTime? lastReadingAt,
        TenantOverviewThresholds thresholds,
        DateTime nowUtc);
}

/// <inheritdoc />
public class GlucoseStatusClassifier : IGlucoseStatusClassifier
{
    private readonly TimeSpan _staleAfter;
    private readonly ILogger<GlucoseStatusClassifier> _logger;

    public GlucoseStatusClassifier(IConfiguration configuration, ILogger<GlucoseStatusClassifier> logger)
    {
        Defaults = new TenantOverviewThresholds(
            UrgentLow: configuration.GetValue("Thresholds:BgLow", ApplicationConstants.Web.Thresholds.BgLow),
            Low: configuration.GetValue("Thresholds:BgTargetBottom", ApplicationConstants.Web.Thresholds.BgTargetBottom),
            High: configuration.GetValue("Thresholds:BgTargetTop", ApplicationConstants.Web.Thresholds.BgTargetTop),
            UrgentHigh: configuration.GetValue("Thresholds:BgHigh", ApplicationConstants.Web.Thresholds.BgHigh));
        _staleAfter = TimeSpan.FromMinutes(configuration.GetValue("Overview:StaleAfterMinutes", 25));
        _logger = logger;
    }

    public TenantOverviewThresholds Defaults { get; }

    public async Task<TenantOverviewThresholds> ResolveThresholdsAsync(NocturneDbContext db, CancellationToken ct)
    {
        var thresholdRules = await db.AlertRules.AsNoTracking()
            .Where(r => r.IsEnabled && r.ConditionType == AlertConditionType.Threshold)
            .ToListAsync(ct);

        return ResolveThresholds(Defaults, ParseThresholdRules(thresholdRules));
    }

    public GlucoseStatus Classify(
        double? mgdl,
        DateTime? readingTimestamp,
        DateTime? lastReadingAt,
        TenantOverviewThresholds thresholds,
        DateTime nowUtc) =>
        Classify(mgdl, readingTimestamp, lastReadingAt, thresholds, _staleAfter, nowUtc);

    private IEnumerable<(string Direction, double Value, AlertRuleSeverity Severity)> ParseThresholdRules(
        IEnumerable<AlertRuleEntity> rules)
    {
        foreach (var rule in rules)
        {
            ThresholdCondition? condition;
            try
            {
                condition = JsonSerializer.Deserialize<ThresholdCondition>(rule.ConditionParams, EvaluatorJson.Options);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Unparseable threshold condition params for rule {RuleId}", rule.Id);
                continue;
            }

            // STJ materializes the record with a null Direction when the property is
            // absent from the JSON; such a rule cannot be bucketed.
            if (condition is null || string.IsNullOrEmpty(condition.Direction))
            {
                if (condition is not null)
                    _logger.LogWarning("Threshold rule {RuleId} has no direction; skipped", rule.Id);
                continue;
            }

            yield return (condition.Direction, (double)condition.Value, rule.Severity);
        }
    }

    /// <summary>
    /// Overrides configuration defaults from the tenant's enabled threshold rules.
    /// Multiple rules in the same bucket resolve to the most conservative value
    /// (below: highest, above: lowest); urgent bounds are then clamped so
    /// UrgentLow &lt;= Low and High &lt;= UrgentHigh, and finally Low is clamped
    /// to High so the in-range band cannot invert.
    /// </summary>
    internal static TenantOverviewThresholds ResolveThresholds(
        TenantOverviewThresholds defaults,
        IEnumerable<(string Direction, double Value, AlertRuleSeverity Severity)> rules)
    {
        double? urgentLow = null, low = null, high = null, urgentHigh = null;

        foreach (var (direction, value, severity) in rules)
        {
            if (string.IsNullOrEmpty(direction)) continue;

            switch (direction.ToLowerInvariant(), severity)
            {
                case ("below", AlertRuleSeverity.Critical):
                    urgentLow = Math.Max(urgentLow ?? double.MinValue, value);
                    break;
                case ("below", _):
                    low = Math.Max(low ?? double.MinValue, value);
                    break;
                case ("above", AlertRuleSeverity.Critical):
                    urgentHigh = Math.Min(urgentHigh ?? double.MaxValue, value);
                    break;
                case ("above", _):
                    high = Math.Min(high ?? double.MaxValue, value);
                    break;
            }
        }

        var resolved = new TenantOverviewThresholds(
            urgentLow ?? defaults.UrgentLow,
            low ?? defaults.Low,
            high ?? defaults.High,
            urgentHigh ?? defaults.UrgentHigh);

        resolved = resolved with
        {
            UrgentLow = Math.Min(resolved.UrgentLow, resolved.Low),
            UrgentHigh = Math.Max(resolved.UrgentHigh, resolved.High),
        };

        // A "below" rule above the high bound (e.g. below-200 with High=180) would invert
        // the in-range band; keep Low <= High (and UrgentLow <= the clamped Low) so
        // classification stays ordered.
        var clampedLow = Math.Min(resolved.Low, resolved.High);
        return resolved with
        {
            Low = clampedLow,
            UrgentLow = Math.Min(resolved.UrgentLow, clampedLow),
        };
    }

    /// <summary>
    /// Boundary values are in range / non-urgent (strict comparisons throughout).
    /// </summary>
    internal static GlucoseStatus Classify(
        double? mgdl,
        DateTime? readingTimestamp,
        DateTime? lastReadingAt,
        TenantOverviewThresholds thresholds,
        TimeSpan staleAfter,
        DateTime nowUtc)
    {
        var freshness = readingTimestamp ?? lastReadingAt;
        if (freshness is null) return GlucoseStatus.Unknown;
        if (nowUtc - freshness.Value > staleAfter) return GlucoseStatus.Stale;
        if (mgdl is null) return GlucoseStatus.Unknown;

        return mgdl.Value switch
        {
            var v when v < thresholds.UrgentLow => GlucoseStatus.UrgentLow,
            var v when v < thresholds.Low => GlucoseStatus.Low,
            var v when v > thresholds.UrgentHigh => GlucoseStatus.UrgentHigh,
            var v when v > thresholds.High => GlucoseStatus.High,
            _ => GlucoseStatus.InRange,
        };
    }
}
