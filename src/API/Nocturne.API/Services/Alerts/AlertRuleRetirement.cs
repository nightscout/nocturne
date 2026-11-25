using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Closes a retiring rule's open excursion and resolves its instance
/// (docs/alerts/engine-semantics.md §6.2), and resolves its standing notice that it cannot be
/// evaluated. Every writer that disables or deletes a rule calls this.
/// </summary>
public sealed class AlertRuleRetirement(
    IExcursionTracker excursionTracker,
    IExcursionResolutionHandler resolutionHandler,
    IUnevaluableRuleNotice unevaluableNotice)
{
    /// <summary>
    /// Force-closes each rule's excursion with <see cref="ExcursionCloseReason.RuleDisabled"/> and
    /// hands the transition to <see cref="IExcursionResolutionHandler"/>, which ignores a rule that
    /// held no excursion, then resolves the rule's standing notice for the same reason.
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
            await unevaluableNotice.ResolveAsync(ruleId, ExcursionCloseReason.RuleDisabled, ct);
        }
    }
}
