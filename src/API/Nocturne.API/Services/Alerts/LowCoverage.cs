using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Whether a tenant has an alert on for low glucose. A rule covers lows when it is enabled, its
/// body evaluates, its <see cref="RuleScopeClass"/> is <see cref="RuleScopeClass.Low"/> or
/// <see cref="RuleScopeClass.Composite"/>, and it has a channel that delivers
/// (<see cref="AlertChannelStates"/>). Severity does not matter. A channel that delivers is one
/// whose recipient can receive and that has not stopped, not one that was verified to arrive. A
/// rule whose issues could not be determined does not cover.
/// </summary>
public static class LowCoverage
{
    /// <summary>
    /// Whether <paramref name="rule"/> covers lows, given the stored issues of its trees, or null
    /// when the engine could not check them.
    /// </summary>
    public static bool Covers(
        AlertRuleRecord rule, IReadOnlyList<RustValidationIssue>? issues, AlertChannelStates states) =>
        issues is not null
        && rule.IsEnabled
        && rule.Rule.ScopeClass is RuleScopeClass.Low or RuleScopeClass.Composite
        && rule.Channels.Any(c => states.Of(c).IsActive)
        && AlertRuleConditionValidator.IsEvaluable(issues);

    /// <summary>The ids of the rules that cover lows.</summary>
    public static IReadOnlySet<Guid> CoveringRuleIds(
        IEnumerable<(AlertRuleRecord Rule, IReadOnlyList<RustValidationIssue>? Issues)> rules,
        AlertChannelStates states) =>
        rules.Where(r => Covers(r.Rule, r.Issues, states)).Select(r => r.Rule.Rule.Id).ToHashSet();
}
