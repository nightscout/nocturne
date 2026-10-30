using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.SetupHub.Items;

/// <summary>
/// Done once someone else can get in: a member invite that is live or was accepted, a guest or
/// follower link that is live, the public link switched on, or a second person already a member.
/// A revoked or lapsed link never counts, since done never reverts. "Just me" is
/// <see cref="SetupHubItemState.NotForMe"/>, so it gives way to done if the owner shares after all.
/// A helper's tenant is shared by handing it over, so it is done only once the recipient owns it
/// (<see cref="Handover.RecipientJoinedAsync"/>); an invite they have not accepted yet is not enough.
/// </summary>
public class SharingItem(
    INocturneDbContextFactory contexts,
    IMemberInviteRepository invites,
    IOAuthGrantRepository grants,
    TimeProvider time) : ISetupHubItem
{
    public SetupHubItemKey Key => SetupHubItemKey.Sharing;

    public async Task<bool> WorksAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var relationship = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == db.TenantId)
            .Select(t => t.PatientRelationship)
            .FirstOrDefaultAsync(ct);
        if (relationship == PatientRelationship.Helper)
            return await Handover.RecipientJoinedAsync(db, db.TenantId, await Handover.HelperAsync(db, db.TenantId, ct), ct);

        var now = time.GetUtcNow().UtcDateTime;
        return await invites.AnyUsedOrLiveAsync(now, ct)
            || await grants.HasLiveShareGrantAsync(now, ct)
            || await db.Tenants.AnyAsync(t => t.Id == db.TenantId && t.ShareToken != null, ct)
            || await db.TenantMembers.CountAsync(
                m => m.TenantId == db.TenantId && !m.Subject!.IsSystemSubject, ct) > 1;
    }
}
