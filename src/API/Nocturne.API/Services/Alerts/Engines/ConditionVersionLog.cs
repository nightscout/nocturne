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

    /// <summary>
    /// True the first time it is asked about this rule with this condition, and again after the
    /// condition changes.
    /// </summary>
    public bool FirstFor(Guid ruleId, AlertConditionType conditionType, string conditionParams)
    {
        var version = (conditionType, conditionParams);
        if (_logged.TryGetValue(ruleId, out var seen) && seen == version)
            return false;
        _logged[ruleId] = version;
        return true;
    }
}
