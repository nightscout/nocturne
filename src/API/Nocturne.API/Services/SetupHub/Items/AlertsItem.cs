using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>Done once the tenant has an enabled alert rule of its own; tracker-managed rules do not count.</summary>
public class AlertsItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Alerts;

    public Task<bool> WorksAsync(CancellationToken ct) =>
        db.AlertRules.AnyAsync(r => r.ManagedBy == null && r.IsEnabled, ct);
}
