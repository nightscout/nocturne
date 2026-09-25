using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Closes a retiring rule's open excursion and resolves its instance
/// (docs/alerts/engine-semantics.md §6.2). Every writer that disables or deletes a rule calls this.
/// </summary>
public sealed class AlertRuleRetirement(
    IExcursionTracker excursionTracker,
    IExcursionResolutionHandler resolutionHandler)
{
    /// <summary>
    /// Force-closes each rule's excursion with <see cref="ExcursionCloseReason.RuleDisabled"/> and
    /// hands the transition to <see cref="IExcursionResolutionHandler"/>, which ignores a rule that
    /// held no excursion.
    /// </summary>
    /// <remarks>
    /// For a delete the rule must still exist: once it is gone the FK cascade takes the excursion
    /// with it and there is nothing left to close.
    /// </remarks>
    public async Task CloseAsync(
        IReadOnlyCollection<Guid> ruleIds, Guid tenantId, CancellationToken ct)
    {
        foreach (var ruleId in ruleIds)
        {
            var transition = await excursionTracker.ForceCloseAsync(
                ruleId, ExcursionCloseReason.RuleDisabled, ct);
            await resolutionHandler.HandleClosedAsync(transition, tenantId, ct);
        }
    }
}
