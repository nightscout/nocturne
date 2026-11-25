using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Removes a membership's direct reason with what it leaves behind: its roles and their audit rows,
/// the subject's chat links to the tenant, every live grant of the subject on it, every live guest
/// link the subject made, whoever the link's data owner is, and every live member invite the subject
/// made. A member whose only reason is direct loses the membership; one who also holds a team's
/// reason keeps it for that reason, and loses the rest all the same, since it was made under the
/// direct membership. What the subject made and does not lose (<see cref="MemberCreations"/>) lasts
/// as long as their access: a kept membership caps it at the end it keeps, and a membership that
/// goes stops it at once when its direct reason had an end date, and leaves it otherwise, as a member
/// who never had an end date made it to last.
/// </summary>
/// <remarks>
/// Chat identity directory rows are global and carry no membership join, so a link left behind
/// would keep answering bot commands for the tenant; the same subject's links to tenants they still
/// belong to survive. Grants carry no membership join either, so left live every one would work
/// again the moment the subject joins again. What the member made is stopped through
/// <see cref="MadeByStop"/>, its audit rows staged before the membership's own rows so its removed row
/// follows them. All of it goes in one save on the caller's unit, so the pieces cannot come apart. A
/// membership whose end date passes is not removed, so the invites its member made are refused at
/// accept instead (<see cref="IMemberInviteRepository.TryClaimUseAsync"/>); deleting the account
/// revokes them (<see cref="SubjectDeletion"/>).
/// </remarks>
internal static class MembershipRemoval
{
    /// <summary>
    /// Stages and saves the removal on <paramref name="unit"/>, inside its commit, and returns the
    /// grants it revoked or capped for <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.
    /// Nothing is staged when <see cref="TenantMembershipRepository.RemoveMemberUnlessLastOwnerAsync"/>
    /// refuses, which it does with <see cref="OwnerGuardedChange.FromTeam"/> for a member with no
    /// direct reason. <paramref name="members"/> is a repository over <paramref name="unit"/>. The
    /// removed row records <paramref name="how"/>.
    /// </summary>
    public static async Task<(OwnerGuardedChange Outcome, IReadOnlyList<OAuthGrantRecord> Grants)> StageAsync(
        DbUnitOfWork unit,
        TenantMembershipRepository members,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        TenantMemberRecord member,
        MembershipEndHow how,
        DateTime now,
        CancellationToken ct)
    {
        var context = unit.Context;
        var made = await MadeByStop.StageAsync(unit, tenantId, member.SubjectId, now, ct);

        var removal = await members.RemoveMemberUnlessLastOwnerAsync(tenantId, member.Id, how, now, ct);
        if (removal.Outcome != OwnerGuardedChange.Changed)
        {
            made.Unstage();
            return (removal.Outcome, []);
        }

        var capAt = removal.MembershipKept
            ? removal.KeptAccessEndsAt
            : member.DirectReason?.AccessEndsAt is not null ? now : null;
        var revoked = await StageLossAsync(unit, made, grantRevocation, tenantId, member.SubjectId, capAt, ct);
        await context.SaveChangesAsync(ct);
        return (removal.Outcome, revoked);
    }

    /// <summary>
    /// Stages on <paramref name="unit"/>, without saving, what <paramref name="subjectId"/> loses with
    /// access on <paramref name="tenantId"/> beside its roles: their chat links to the tenant, every live
    /// grant of theirs on it, and the rest of <paramref name="made"/>, capped at <paramref name="capAt"/>
    /// (<see cref="MadeByStop.FinishAsync"/>). The unit must be pinned to the tenant.
    /// </summary>
    /// <returns>
    /// The grants it revoked or capped, for <see cref="GrantRevocationService.EvictSessions"/> once the unit commits.
    /// </returns>
    public static async Task<IReadOnlyList<OAuthGrantRecord>> StageLossAsync(
        DbUnitOfWork unit,
        MadeByStop made,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        Guid subjectId,
        DateTime? capAt,
        CancellationToken ct)
    {
        var context = unit.Context;
        var chatLinks = await context.ChatIdentityDirectory
            .Where(d => d.TenantId == tenantId && d.NocturneUserId == subjectId)
            .ToListAsync(ct);
        var owned = await new OAuthGrantRepository(unit).GetActiveForSubjectAsync(subjectId, ct);
        context.ChatIdentityDirectory.RemoveRange(chatLinks);
        return await made.FinishAsync(grantRevocation, owned, capAt, ct);
    }

    /// <summary>
    /// Removes <paramref name="subjectId"/>'s direct reason on <paramref name="tenantId"/> when it has
    /// ended at <paramref name="now"/>, so a direct reason that replaces it can be added in the same
    /// commit without meeting the old one. Returns the grants it revoked, none when there was no direct
    /// reason, and null when the subject holds one that has not ended.
    /// </summary>
    /// <remarks>
    /// What the person made lives as long as their access as a whole. While another reason is in
    /// force, their access never stopped, so only the reason and its audit rows go; otherwise the whole
    /// of <see cref="StageAsync"/> runs.
    /// </remarks>
    public static async Task<IReadOnlyList<OAuthGrantRecord>?> ReplaceEndedAsync(
        DbUnitOfWork unit,
        TenantMembershipRepository members,
        GrantRevocationService grantRevocation,
        Guid tenantId,
        Guid subjectId,
        DateTime now,
        CancellationToken ct)
    {
        if (await members.FindMemberAsync(tenantId, subjectId, ct) is not { DirectReason: { } direct } existing)
            return [];
        if (!MembershipLiveness.HasEnded(direct.AccessEndsAt, now))
            return null;

        if (existing.HeldReasons.Any(r => r.Kind != MembershipReasonKind.Direct && !MembershipLiveness.HasEnded(r.AccessEndsAt, now)))
        {
            if ((await members.RemoveMemberUnlessLastOwnerAsync(tenantId, existing.Id, MembershipEndHow.Replaced, now, ct)).Outcome
                != OwnerGuardedChange.Changed)
                return null;
            await unit.Context.SaveChangesAsync(ct);
            return [];
        }

        var (outcome, grants) = await StageAsync(
            unit, members, grantRevocation, tenantId, existing, MembershipEndHow.Replaced, now, ct);
        return outcome == OwnerGuardedChange.Changed ? grants : null;
    }
}
