using System.Collections.Concurrent;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Which version of each rule's condition a once-per-version log line has been written for, so
/// a stored rule that fails the same way on every evaluation is reported once until it is edited.
/// </summary>
internal sealed class ConditionVersionLog
{
    private readonly ConcurrentDictionary<Guid, (AlertConditionType Type, string Params)> _logged = new();
    private readonly ConcurrentDictionary<Guid, (AlertConditionType Type, string Params, IReadOnlyList<RustValidationIssue>? Issues)> _issues = new();

    /// <summary>
    /// True the first time it is asked about this rule with this condition, and again after the
    /// condition changes.
    /// </summary>
    public bool FirstFor(Guid ruleId, AlertConditionType conditionType, string conditionParams) =>
        First(_logged, ruleId, conditionType, conditionParams);

    /// <summary>
    /// The problems <paramref name="validate"/> finds with the rule's trees at this version of its
    /// condition, validated once per version; <paramref name="validated"/> is true for the call that
    /// validated it. A validation that threw is kept as null, so it is neither repeated nor reported
    /// again until the condition changes. Kept apart from <see cref="FirstFor"/>, so the check never
    /// takes the engines' one report of a failure.
    /// </summary>
    public IReadOnlyList<RustValidationIssue>? IssuesFor(
        Guid ruleId,
        AlertConditionType conditionType,
        string conditionParams,
        Func<IReadOnlyList<RustValidationIssue>> validate,
        out bool validated)
    {
        if (_issues.TryGetValue(ruleId, out var kept) && kept.Type == conditionType && kept.Params == conditionParams)
        {
            validated = false;
            return kept.Issues;
        }

        validated = true;
        _issues[ruleId] = (conditionType, conditionParams, null);
        var issues = validate();
        _issues[ruleId] = (conditionType, conditionParams, issues);
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
