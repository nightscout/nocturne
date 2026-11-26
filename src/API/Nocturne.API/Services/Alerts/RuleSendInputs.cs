using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// What a send to a rule's channels reads besides its context: the channels, the tenant's count of
/// open excursions and its alert context, which its payload carries. Read by an opening
/// (<see cref="IExcursionOpenedHandler"/>), a snooze's re-notification
/// (<see cref="IAlertSnoozeService.ResumeAsync"/>) and a notice's dispatch (<see cref="RuleNoticeDispatch"/>).
/// The channels are empty when the rule has none, and the send still goes on.
/// </summary>
internal sealed record RuleSendInputs(
    IReadOnlyList<AlertRuleChannelSnapshot> Channels,
    int ActiveExcursionCount,
    TenantAlertContext? Tenant)
{
    public static async Task<RuleSendInputs> ReadAsync(
        Guid ruleId,
        Guid tenantId,
        IAlertRepository repository,
        IAlertRuleRepository ruleRepository,
        CancellationToken ct) =>
        new((await ruleRepository.GetChannelsAsync([ruleId], ct)).GetValueOrDefault(ruleId) ?? [],
            await repository.CountActiveExcursionsAsync(ct),
            await repository.GetTenantAlertContextAsync(tenantId, ct));
}
