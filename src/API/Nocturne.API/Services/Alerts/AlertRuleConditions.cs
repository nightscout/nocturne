using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The fields of a rule a tracker decision is made against and whose change clears the rule's
/// re-arm hold (docs/alerts/engine-semantics.md §6.3, <see cref="AlertRuleRearm"/>): its
/// enablement, condition and auto-resolve configuration. <see cref="ExcursionTransitionWriter"/>
/// drops a decision made against fields the rule no longer holds.
/// </summary>
internal readonly record struct AlertRuleConditions(
    bool IsEnabled,
    AlertConditionType ConditionType,
    string ConditionParams,
    bool AutoResolveEnabled,
    string? AutoResolveParams)
{
    /// <summary>The fields of an evaluated snapshot.</summary>
    /// <remarks>
    /// A snapshot carries no enabled flag: every snapshot an engine evaluates is loaded from the
    /// enabled rules only (<see cref="AlertRepository.GetEnabledRulesAsync"/>,
    /// <see cref="AlertRepository.GetAllEnabledRulesAsync"/>,
    /// <see cref="AlertRepository.GetAutoResolveExcursionsAsync"/>), so it reads as enabled.
    /// </remarks>
    public static AlertRuleConditions Of(AlertRuleSnapshot rule) =>
        new(true, rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled, rule.AutoResolveParams);

    /// <summary>The fields of a stored rule, for a decision made on the row itself.</summary>
    public static AlertRuleConditions Of(AlertRule rule) =>
        new(rule.IsEnabled, rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled, rule.AutoResolveParams);

    /// <summary>
    /// Whether <paramref name="rule"/> still holds these fields, its trees compared as JSON as a
    /// rule edit compares them (<see cref="ConditionTreeEquality"/>). A disabled rule holds none:
    /// no evaluation's decision lands on it.
    /// </summary>
    public bool HeldBy(AlertRule? rule) =>
        rule is { IsEnabled: true }
        && IsEnabled
        && rule.ConditionType == ConditionType
        && rule.AutoResolveEnabled == AutoResolveEnabled
        && ConditionTreeEquality.Same(rule.ConditionParams, ConditionParams)
        && ConditionTreeEquality.Same(rule.AutoResolveParams, AutoResolveParams);
}
