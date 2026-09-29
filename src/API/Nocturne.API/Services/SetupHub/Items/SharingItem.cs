using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>Done once someone was invited or the public link was switched on.</summary>
public class SharingItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Sharing;

    public async Task<bool> WorksAsync(CancellationToken ct) =>
        await db.MemberInvites.AnyAsync(ct)
        || await db.Tenants.AnyAsync(t => t.Id == db.TenantId && t.ShareToken != null, ct);
}
