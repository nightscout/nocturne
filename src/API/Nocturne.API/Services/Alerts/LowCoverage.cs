using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Whether a tenant has an alert on for low glucose. A rule covers lows when it is enabled, its
/// body evaluates, its <see cref="RuleScopeClass"/> is <see cref="RuleScopeClass.Low"/> or
/// <see cref="RuleScopeClass.Composite"/>, and it has a delivery channel. Severity does not matter.
/// A channel is a configured one, not a verified one. A rule whose issues could not be determined
/// does not cover.
/// </summary>
public static class LowCoverage
{
    /// <summary>
    /// Whether <paramref name="rule"/> covers lows, given the stored issues of its trees, or null
    /// when the engine could not check them.
    /// </summary>
    public static bool Covers(AlertRuleRecord rule, IReadOnlyList<RustValidationIssue>? issues) =>
        issues is not null
        && rule.IsEnabled
        && rule.Rule.ScopeClass is RuleScopeClass.Low or RuleScopeClass.Composite
        && rule.Channels.Count > 0
        && AlertRuleConditionValidator.IsEvaluable(issues);

    /// <summary>The ids of the rules that cover lows.</summary>
    public static IReadOnlySet<Guid> CoveringRuleIds(
        IEnumerable<(AlertRuleRecord Rule, IReadOnlyList<RustValidationIssue>? Issues)> rules) =>
        rules.Where(r => Covers(r.Rule, r.Issues)).Select(r => r.Rule.Rule.Id).ToHashSet();
}
