using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// A stored rule and the body it was classified from, which <see cref="IAlertRuleRepository.SetScopeClassAsync"/>
/// compares to what the rule holds when the class is written.
/// </summary>
public sealed record ClassifiedRule(Guid Id, AlertConditionType ConditionType, string ConditionParams);
