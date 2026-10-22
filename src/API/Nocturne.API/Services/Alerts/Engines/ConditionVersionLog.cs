using System.Collections.Concurrent;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Which version of each rule's condition a once-per-version log line has been written for, so
/// a stored rule that fails the same way on every evaluation is reported once until it is edited.
/// </summary>
internal sealed class ConditionVersionLog
{
    private readonly ConcurrentDictionary<Guid, (AlertConditionType Type, string Params)> _logged = new();
    private readonly ConcurrentDictionary<Guid, (AlertConditionType Type, string Params)> _issuesChecked = new();

    /// <summary>
    /// True the first time it is asked about this rule with this condition, and again after the
    /// condition changes.
    /// </summary>
    public bool FirstFor(Guid ruleId, AlertConditionType conditionType, string conditionParams) =>
        First(_logged, ruleId, conditionType, conditionParams);

    /// <summary>
    /// <see cref="FirstFor"/> for the orchestrator's check of a rule's conditions against what a
    /// save rejects, kept apart so that check never takes the engines' one report of a failure.
    /// </summary>
    public bool FirstIssueCheckFor(Guid ruleId, AlertConditionType conditionType, string conditionParams) =>
        First(_issuesChecked, ruleId, conditionType, conditionParams);

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
