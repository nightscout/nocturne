using System.Collections.Concurrent;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Which version of each rule's condition a once-per-version log line has been written for, so
/// a stored rule that fails the same way on every evaluation is reported once until it is edited.
/// </summary>
internal sealed class ConditionVersionLog
{
    private readonly ConcurrentDictionary<Guid, (AlertConditionType Type, string Params)> _logged = new();
    private readonly ConcurrentDictionary<Guid, (RuleTrees Trees, IReadOnlyList<RustValidationIssue>? Issues)> _issues = new();

    /// <summary>Every stored value of a rule that its validation reads.</summary>
    private readonly record struct RuleTrees(
        AlertConditionType Type, string Params, bool AutoResolveEnabled, string? AutoResolveParams, string ClientConfiguration)
    {
        public static RuleTrees Of(AlertRuleSnapshot rule) =>
            new(rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled, rule.AutoResolveParams, rule.ClientConfiguration);
    }

    /// <summary>
    /// True the first time it is asked about this rule with this condition, and again after the
    /// condition changes.
    /// </summary>
    public bool FirstFor(Guid ruleId, AlertConditionType conditionType, string conditionParams) =>
        First(_logged, ruleId, conditionType, conditionParams);

    /// <summary>
    /// The problems <paramref name="validate"/> finds with the rule's trees, validated once per
    /// version of its body, auto-resolve tree and client configuration (which holds the snooze
    /// conditions); <paramref name="validated"/> is true for the call that validated it. A validation
    /// that threw is kept as null, so it is neither repeated nor reported again until one of them
    /// changes. Kept apart from <see cref="FirstFor"/>, so the check never takes the engines' one
    /// report of a failure.
    /// </summary>
    public IReadOnlyList<RustValidationIssue>? IssuesFor(
        AlertRuleSnapshot rule,
        Func<IReadOnlyList<RustValidationIssue>> validate,
        out bool validated)
    {
        var trees = RuleTrees.Of(rule);
        if (_issues.TryGetValue(rule.Id, out var kept) && kept.Trees == trees)
        {
            validated = false;
            return kept.Issues;
        }

        validated = true;
        _issues[rule.Id] = (trees, null);
        var issues = validate();
        _issues[rule.Id] = (trees, issues);
        return issues;
    }

    private static bool First(
        ConcurrentDictionary<Guid, (AlertConditionType Type, string Params)> seen,
        Guid ruleId,
        AlertConditionType conditionType,
        string conditionParams)
    {
        var version = (conditionType, conditionParams);
        if (seen.TryGetValue(ruleId, out var last) && last == version)
            return false;
        seen[ruleId] = version;
        return true;
    }
}
