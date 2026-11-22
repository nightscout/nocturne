using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Removes a membership with what it leaves behind: its roles and their audit rows, the subject's
/// chat links to the tenant, every live grant of the subject on it, and every live guest link the
/// subject made, whoever the link's data owner is.
/// </summary>
/// <remarks>
/// Chat identity directory rows are global and carry no membership join, so a link left behind
/// would keep answering bot commands for the tenant; the same subject's links to tenants they still
/// belong to survive. Grants carry no membership join either, so left live every one would work
/// again the moment the subject joins again. A guest link's grant belongs to its data owner, so it is
/// found by the member who made it, and each one revoked is audited. All of it goes in one save on
/// the caller's unit, so the pieces cannot come apart.
/// </remarks>
internal static class MembershipRemoval
{
    /// <summary>
    /// Stages and saves the removal on <paramref name="unit"/>, inside its commit, and returns the
    /// grants it revoked for <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.
    /// Nothing is staged when <see cref="ITenantMembershipRepository.RemoveMemberUnlessLastOwnerAsync"/>
    /// refuses. <paramref name="members"/> is a repository over <paramref name="unit"/>.
    /// </summary>
    public static async Task<(OwnerGuardedChange Outcome, IReadOnlyList<OAuthGrantRecord> Grants)> StageAsync(
        DbUnitOfWork unit,
        ITenantMembershipRepository members,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        TenantMemberRecord member,
        DateTime now,
        CancellationToken ct)
    {
        var removal = await members.RemoveMemberUnlessLastOwnerAsync(tenantId, member.Id, ct);
        if (removal != OwnerGuardedChange.Changed)
            return (removal, []);

        var context = unit.Context;
        var chatLinks = await context.ChatIdentityDirectory
            .Where(d => d.TenantId == tenantId && d.NocturneUserId == member.SubjectId)
            .ToListAsync(ct);
        var grants = new OAuthGrantRepository(unit);
        var owned = await grants.GetActiveForSubjectAsync(member.SubjectId, ct);
        var handedOut = await grants.GetLiveGuestLinksCreatedByAsync(member.SubjectId, now, ct);
        var revoked = owned.Concat(handedOut.ExceptBy(owned.Select(g => g.Id), g => g.Id)).ToList();

        context.ChatIdentityDirectory.RemoveRange(chatLinks);
        await grantRevocation.StageAsync(unit, revoked, ct);
        grants.StageMemberRemovalGuestLinkAudit(handedOut, member.SubjectId);
        await context.SaveChangesAsync(ct);
        return (removal, revoked);
    }

    /// <summary>
    /// Removes <paramref name="subjectId"/>'s membership of <paramref name="tenantId"/> through
    /// <see cref="StageAsync"/> when it has ended at <paramref name="now"/>, so a membership that
    /// replaces it can be added in the same commit without meeting the old row. Returns the grants it
    /// revoked, none when there was no membership, and null when the subject holds one that has not
    /// ended.
    /// </summary>
    public static async Task<IReadOnlyList<OAuthGrantRecord>?> ReplaceEndedAsync(
        DbUnitOfWork unit,
        ITenantMembershipRepository members,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        Guid subjectId,
        DateTime now,
        CancellationToken ct)
    {
        if (await members.FindMemberAsync(tenantId, subjectId, ct) is not { } existing)
            return [];
        if (!MembershipLiveness.HasEnded(existing.AccessEndsAt, now))
            return null;

        var (outcome, grants) = await StageAsync(unit, members, grantRevocation, tenantId, existing, now, ct);
        return outcome == OwnerGuardedChange.Changed ? grants : null;
    }
}
