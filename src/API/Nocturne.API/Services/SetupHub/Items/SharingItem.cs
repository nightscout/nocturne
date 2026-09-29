using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once an invite was accepted or is still open, or the public link is on. A revoked or
/// lapsed invite nobody used shares nothing, and done never reverts, so it must not count.
/// </summary>
public class SharingItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Sharing;

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.MemberInvites.AnyAsync(
                i => i.UseCount > 0 || (i.RevokedAt == null && i.ExpiresAt > now), ct)
            || await db.Tenants.AnyAsync(t => t.Id == db.TenantId && t.ShareToken != null, ct);
    }
}
