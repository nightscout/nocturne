using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once someone else can get in: a member invite that is live or was accepted, a guest or
/// follower link that is live, the public link switched on, or a second person already a member.
/// A revoked or lapsed link never counts, since done never reverts. "Just me" is
/// <see cref="SetupHubItemState.NotForMe"/>, so it gives way to done if the owner shares after all.
/// </summary>
public class SharingItem(NocturneDbContext db) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Sharing;

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.MemberInvites.AnyAsync(
                i => i.UseCount > 0
                    || (i.RevokedAt == null && i.ExpiresAt > now && (i.MaxUses == null || i.UseCount < i.MaxUses)),
                ct)
            || await db.OAuthGrants.AnyAsync(
                g => (g.GrantType == OAuthGrantTypes.Guest || g.GrantType == OAuthGrantTypes.Follower)
                    && g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > now),
                ct)
            || await db.Tenants.AnyAsync(t => t.Id == db.TenantId && t.ShareToken != null, ct)
            || await db.TenantMembers.CountAsync(
                m => m.TenantId == db.TenantId && !m.Subject!.IsSystemSubject, ct) > 1;
    }
}
