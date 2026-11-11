using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once <see cref="AlertDeliveryCheck.VerifiedAsync"/> says the tenant's alerts reach someone.
/// A rule alone is not enough: nothing says its alerts reach anyone. A helper is offered it too and
/// leaves it for the person they hand over to, since a helper cannot confirm a test.
/// </summary>
public class AlertsItem(INocturneDbContextFactory contexts, ITenantRepository tenants, IAlertRuleRepository rules) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Alerts;

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await AlertDeliveryCheck.VerifiedAsync(db, tenants, rules, ct);
    }
}
