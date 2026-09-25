using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Closes each just-disabled rule's open excursion and resolves its instance
/// (docs/alerts/engine-semantics.md §6.2). Every writer that disables a rule calls this after the
/// save lands.
/// </summary>
public sealed class AlertRuleDisableHandler(
    IExcursionTracker excursionTracker,
    IExcursionResolutionHandler resolutionHandler)
{
    /// <summary>
    /// Force-closes each rule's excursion with <see cref="ExcursionCloseReason.RuleDisabled"/> and
    /// hands the transition to <see cref="IExcursionResolutionHandler"/>, which ignores a rule that
    /// held no excursion.
    /// </summary>
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
