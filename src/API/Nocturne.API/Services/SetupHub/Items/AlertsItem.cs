using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once an enabled rule of the tenant's own has a test alert the owner confirmed arrived
/// (<see cref="AlertSetupService.ConfirmReceivedAsync"/>). A rule alone is not enough: nothing says
/// its alerts reach anyone. A helper is offered it too and leaves it for the person they hand over
/// to, since a helper cannot confirm a test.
/// </summary>
public class AlertsItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Alerts;

    public Task<bool> WorksAsync(CancellationToken ct) =>
        db.AlertInstances.AnyAsync(
            i => i.IsTest
                 && i.ReceiptConfirmedAt != null
                 && i.AlertExcursion!.AlertRule!.IsEnabled
                 && i.AlertExcursion.AlertRule.ManagedBy == null,
            ct);
}
