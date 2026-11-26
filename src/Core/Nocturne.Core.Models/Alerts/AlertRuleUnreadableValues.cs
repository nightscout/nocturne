namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The columns of a stored rule whose value names no member, so the rule is shown with a stand-in
/// for each and is not evaluated until a save stores a member.
/// </summary>
[Flags]
public enum AlertRuleUnreadableValues
{
    None = 0,
    ConditionType = 1,
    Severity = 2,
    ScopeClass = 4,
}
