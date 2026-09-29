using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once <see cref="AlertDeliveryCheck.VerifiedAsync"/> says the tenant's alerts reach someone.
/// A rule alone is not enough: nothing says its alerts reach anyone. A helper is offered it too and
/// leaves it for the person they hand over to, since a helper cannot confirm a test.
/// </summary>
public class AlertsItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Alerts;

    public Task<bool> WorksAsync(CancellationToken ct) => AlertDeliveryCheck.VerifiedAsync(db, ct);
}
