using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The one writer of a membership's access: it alone adds or removes a <c>tenant_members</c> row or
/// its role links, and sets the row's direct permissions, 24-hour limit, end date and invite. Each
/// change is made to a reason (<c>tenant_member_reasons</c>), after which the row and its role links
/// are projected to the <see cref="MembershipSum"/> of the member's reasons, on the same context, so
/// one save commits both.
/// </summary>
/// <remarks>
/// <para>
/// Audit rows are not staged here: <see cref="MemberAccessAudit"/> stages them from the change the
/// caller asked for, before the call.
/// </para>
/// <para>
/// Before a change, the row and its reasons are brought together by <see cref="AlignWithRow"/>: a row
/// with no reason (written by an older build, or seeded by a test) is adopted, and a direct reason the
/// row no longer matches is re-synced from the row, so the change starts from what the row gives.
/// </para>
/// <para>
/// The row matches its reasons when it holds the same set of roles, the same direct permissions
/// exactly (null against empty, order and repeats all count, because the row stores the list as the
/// reasons give it), the same limit and end, and the direct reason's invite. A projection that finds
/// the row matching writes nothing.
/// </para>
/// <para>
/// Postgres removes a member's reasons by cascade, but removal also removes them explicitly, so a
/// provider that does not cascade to untracked rows leaves none behind.
/// </para>
/// </remarks>
internal static class MembershipReasons
{
    /// <summary>
    /// Adds <paramref name="member"/>, which carries only its row-only state (ids, label, username,
    /// last use), with a direct reason that gives <paramref name="direct"/>.
    /// </summary>
    public static void AddMember(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, Guid? createdFromInviteId)
    {
        db.TenantMembers.Add(member);
        NewDirectReason(db, member.Id, direct.RoleIds, direct.DirectPermissions, direct.LimitTo24Hours,
            direct.AccessEndsAt, createdFromInviteId);
        Project(db, member);
    }

    /// <summary>Sets the direct permissions the member's direct reason gives.</summary>
    public static async Task SetDirectPermissionsAsync(
        NocturneDbContext db, TenantMemberEntity member, IReadOnlyList<string>? permissions, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, ct);
        direct.DirectPermissions = permissions?.ToList();
        Project(db, member);
    }

    /// <summary>Sets whether the member's direct reason limits them to the last 24 hours.</summary>
    public static async Task SetLimitTo24HoursAsync(
        NocturneDbContext db, TenantMemberEntity member, bool limitTo24Hours, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, ct);
        direct.LimitTo24Hours = limitTo24Hours;
        Project(db, member);
    }

    /// <summary>Sets when the member's direct reason ends.</summary>
    public static async Task SetAccessEndAsync(
        NocturneDbContext db, TenantMemberEntity member, DateTime? accessEndsAt, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, ct);
        direct.AccessEndsAt = accessEndsAt;
        Project(db, member);
    }

    /// <summary>
    /// Takes <paramref name="removedRoleIds"/> from the member's direct reason, then gives it
    /// <paramref name="addedRoleIds"/>.
    /// </summary>
    public static async Task ReplaceRolesAsync(
        NocturneDbContext db, TenantMemberEntity member, IReadOnlyCollection<Guid> removedRoleIds,
        IReadOnlyCollection<Guid> addedRoleIds, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, ct);
        db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, direct.Id).Where(rr => removedRoleIds.Contains(rr.TenantRoleId)));
        foreach (var roleId in addedRoleIds)
            db.TenantMemberReasonRoles.Add(NewReasonRole(direct.Id, roleId));
        Project(db, member);
    }

    /// <summary>Removes the member with its reasons and its role links.</summary>
    public static async Task RemoveMemberAsync(NocturneDbContext db, TenantMemberEntity member, CancellationToken ct)
    {
        await LoadAsync(db, member.Id, ct);
        foreach (var reason in ReasonsOf(db, member.Id))
        {
            db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, reason.Id));
            db.TenantMemberReasons.Remove(reason);
        }

        db.TenantMemberRoles.RemoveRange(LinksOf(db, member.Id));
        db.TenantMembers.Remove(member);
    }

    /// <summary>
    /// Takes <paramref name="roleId"/> from every reason that gives it and from every member holding
    /// it, before the role itself is removed.
    /// </summary>
    public static async Task RemoveRoleAsync(NocturneDbContext db, Guid roleId, CancellationToken ct)
    {
        var memberIds = await db.TenantMemberRoles
            .Where(mr => mr.TenantRoleId == roleId)
            .Select(mr => mr.TenantMemberId)
            .Union(db.TenantMemberReasonRoles
                .Where(rr => rr.TenantRoleId == roleId)
                .Select(rr => rr.TenantMemberReason.TenantMemberId))
            .ToListAsync(ct);

        foreach (var memberId in memberIds)
        {
            var member = await db.TenantMembers.FirstAsync(m => m.Id == memberId, ct);
            await DirectReasonAsync(db, member, ct);
            foreach (var reason in ReasonsOf(db, memberId))
                db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, reason.Id).Where(rr => rr.TenantRoleId == roleId));
            Project(db, member);
        }
    }

    /// <summary>
    /// Whether <paramref name="row"/>, holding <paramref name="rowRoleIds"/>, is the sum of
    /// <paramref name="reasons"/> as the remarks define it; never when it has no reason.
    /// </summary>
    internal static bool Matches(
        TenantMemberEntity row, IReadOnlyCollection<Guid> rowRoleIds,
        IReadOnlyList<(MembershipGrant Grant, string Kind, Guid? InviteId)> reasons)
    {
        if (MembershipSum.Of(reasons.Select(r => r.Grant).ToList()) is not { } sum)
            return false;

        return rowRoleIds.ToHashSet().SetEquals(sum.RoleIds)
            && SamePermissions(row.DirectPermissions, sum.DirectPermissions)
            && row.LimitTo24Hours == sum.LimitTo24Hours
            && row.AccessEndsAt == sum.AccessEndsAt
            && row.CreatedFromInviteId == DirectInvite(reasons);
    }

    /// <summary>Whether the member's row is the sum of its reasons, by <see cref="Matches"/>.</summary>
    internal static async Task<bool> MatchesAsync(NocturneDbContext db, Guid memberId, CancellationToken ct)
    {
        var member = await db.TenantMembers.FirstAsync(m => m.Id == memberId, ct);
        await LoadAsync(db, memberId, ct);
        return Matches(
            member,
            LinksOf(db, memberId).Select(mr => mr.TenantRoleId).ToList(),
            ReasonsOf(db, memberId).Select(r => Described(db, r)).ToList());
    }

    /// <summary>
    /// Brings every member row and its reasons together by <see cref="AlignWithRow"/>, for the startup
    /// check, and saves once.
    /// </summary>
    internal static async Task<MembershipReconciliation> ReconcileAsync(NocturneDbContext db, CancellationToken ct)
    {
        var members = await db.TenantMembers.ToListAsync(ct);
        await db.TenantMemberRoles.LoadAsync(ct);
        await db.TenantMemberReasons.LoadAsync(ct);
        await db.TenantMemberReasonRoles.LoadAsync(ct);

        var adopted = 0;
        var resynced = new List<Guid>();
        var left = new List<Guid>();
        foreach (var member in members)
        {
            switch (AlignWithRow(db, member))
            {
                case RowAgreement.Adopted:
                    adopted++;
                    break;
                case RowAgreement.Resynced:
                    resynced.Add(member.Id);
                    break;
                case RowAgreement.Left:
                    left.Add(member.Id);
                    break;
            }
        }

        if (adopted > 0 || resynced.Count > 0)
            await db.SaveChangesAsync(ct);

        return new MembershipReconciliation(adopted, resynced, left);
    }

    /// <summary>
    /// The member's direct reason, after <see cref="AlignWithRow"/> has brought it into line with the
    /// row. Loads the member's reasons and links onto the context. A re-sync is logged at warning and
    /// writes no audit row: no access changed, only the reason had fallen behind the row.
    /// </summary>
    private static async Task<TenantMemberReasonEntity> DirectReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, CancellationToken ct)
    {
        await LoadAsync(db, member.Id, ct);
        if (AlignWithRow(db, member) == RowAgreement.Resynced)
        {
            db.GetService<ILoggerFactory>().CreateLogger(typeof(MembershipReasons)).LogWarning(
                "Re-synced the direct reason of membership {MemberId} from its row, which did not match it.", member.Id);
        }

        return ReasonsOf(db, member.Id).FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct)
            ?? throw new InvalidOperationException($"Member {member.Id} has reasons but no direct reason to change.");
    }

    private enum RowAgreement
    {
        Matches,
        Adopted,
        Resynced,
        Left,
    }

    /// <summary>
    /// Brings a member's loaded reasons into line with its row: a member with no reason is given its
    /// direct reason from the row; a member that does not match its reasons and has only the direct
    /// one has that reason re-synced from the row; any other member that does not match is left as it
    /// is. Nothing on the row changes.
    /// </summary>
    /// <remarks>
    /// The row wins because a build that ran after a rollback, or beside this one, wrote the row and
    /// knew nothing of reasons, so taking the reason would bring back access someone removed. That
    /// holds only while the direct reason is the only one a member holds.
    /// </remarks>
    private static RowAgreement AlignWithRow(NocturneDbContext db, TenantMemberEntity member)
    {
        var reasons = ReasonsOf(db, member.Id);
        if (reasons.Count == 0)
        {
            NewDirectReason(
                db, member.Id, LinksOf(db, member.Id).Select(mr => mr.TenantRoleId).ToList(),
                member.DirectPermissions, member.LimitTo24Hours, member.AccessEndsAt, member.CreatedFromInviteId);
            return RowAgreement.Adopted;
        }

        var links = LinksOf(db, member.Id).Select(mr => mr.TenantRoleId).ToHashSet();
        if (Matches(member, links, reasons.Select(r => Described(db, r)).ToList()))
            return RowAgreement.Matches;
        if (reasons is not [{ Kind: MembershipReasonKind.Direct } direct])
            return RowAgreement.Left;

        var held = RolesOf(db, direct.Id);
        db.TenantMemberReasonRoles.RemoveRange(held.Where(rr => !links.Contains(rr.TenantRoleId)));
        db.TenantMemberReasonRoles.AddRange(links
            .Where(roleId => held.All(rr => rr.TenantRoleId != roleId))
            .Select(roleId => NewReasonRole(direct.Id, roleId)));
        direct.DirectPermissions = member.DirectPermissions?.ToList();
        direct.LimitTo24Hours = member.LimitTo24Hours;
        direct.AccessEndsAt = member.AccessEndsAt;
        direct.CreatedFromInviteId = member.CreatedFromInviteId;
        return RowAgreement.Resynced;
    }

    /// <summary>
    /// Writes the row and its role links to match the member's reasons, touching nothing that
    /// already does.
    /// </summary>
    private static void Project(NocturneDbContext db, TenantMemberEntity member)
    {
        var reasons = ReasonsOf(db, member.Id).Select(r => Described(db, r)).ToList();
        var sum = MembershipSum.Of(reasons.Select(r => r.Grant).ToList())
            ?? throw new InvalidOperationException($"Member {member.Id} has no reason to project.");

        var links = LinksOf(db, member.Id);
        db.TenantMemberRoles.RemoveRange(links.Where(mr => !sum.RoleIds.Contains(mr.TenantRoleId)));
        foreach (var roleId in sum.RoleIds.Distinct().Where(id => links.All(mr => mr.TenantRoleId != id)))
        {
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = roleId,
            });
        }

        if (!SamePermissions(member.DirectPermissions, sum.DirectPermissions))
            member.DirectPermissions = sum.DirectPermissions?.ToList();
        if (member.LimitTo24Hours != sum.LimitTo24Hours)
            member.LimitTo24Hours = sum.LimitTo24Hours;
        if (member.AccessEndsAt != sum.AccessEndsAt)
            member.AccessEndsAt = sum.AccessEndsAt;
        if (DirectInvite(reasons) is var invite && member.CreatedFromInviteId != invite)
            member.CreatedFromInviteId = invite;
    }

    /// <summary>
    /// Puts the member's reasons, their roles and the member's role links on the context, beside any
    /// the context already tracks. A second kind of reason will need the member row locked here, so
    /// two writers cannot each project from reasons the other is changing.
    /// </summary>
    private static async Task LoadAsync(NocturneDbContext db, Guid memberId, CancellationToken ct)
    {
        await db.TenantMemberReasons.Where(r => r.TenantMemberId == memberId).LoadAsync(ct);
        await db.TenantMemberReasonRoles.Where(rr => rr.TenantMemberReason.TenantMemberId == memberId).LoadAsync(ct);
        await db.TenantMemberRoles.Where(mr => mr.TenantMemberId == memberId).LoadAsync(ct);
    }

    private static List<TenantMemberReasonEntity> ReasonsOf(NocturneDbContext db, Guid memberId) =>
        db.TenantMemberReasons.Local.Where(r => r.TenantMemberId == memberId).ToList();

    private static List<TenantMemberReasonRoleEntity> RolesOf(NocturneDbContext db, Guid reasonId) =>
        db.TenantMemberReasonRoles.Local.Where(rr => rr.TenantMemberReasonId == reasonId).ToList();

    private static List<TenantMemberRoleEntity> LinksOf(NocturneDbContext db, Guid memberId) =>
        db.TenantMemberRoles.Local.Where(mr => mr.TenantMemberId == memberId).ToList();

    private static (MembershipGrant Grant, string Kind, Guid? InviteId) Described(
        NocturneDbContext db, TenantMemberReasonEntity reason) =>
        (new MembershipGrant(
            RolesOf(db, reason.Id).Select(rr => rr.TenantRoleId).ToList(),
            reason.DirectPermissions,
            reason.LimitTo24Hours,
            reason.AccessEndsAt), reason.Kind, reason.CreatedFromInviteId);

    private static Guid? DirectInvite(IEnumerable<(MembershipGrant Grant, string Kind, Guid? InviteId)> reasons) =>
        reasons.FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct).InviteId;

    private static bool SamePermissions(IReadOnlyList<string>? row, IReadOnlyList<string>? sum) =>
        row is null ? sum is null : sum is not null && row.SequenceEqual(sum, StringComparer.Ordinal);

    private static TenantMemberReasonEntity NewDirectReason(
        NocturneDbContext db, Guid memberId, IReadOnlyCollection<Guid> roleIds, IReadOnlyList<string>? permissions,
        bool limitTo24Hours, DateTime? accessEndsAt, Guid? createdFromInviteId)
    {
        var reason = new TenantMemberReasonEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            Kind = MembershipReasonKind.Direct,
            DirectPermissions = permissions?.ToList(),
            LimitTo24Hours = limitTo24Hours,
            AccessEndsAt = accessEndsAt,
            CreatedFromInviteId = createdFromInviteId,
        };
        reason.ReasonRoles = roleIds.Distinct().Select(roleId => NewReasonRole(reason.Id, roleId)).ToList();
        db.TenantMemberReasons.Add(reason);
        return reason;
    }

    private static TenantMemberReasonRoleEntity NewReasonRole(Guid reasonId, Guid roleId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantMemberReasonId = reasonId,
        TenantRoleId = roleId,
    };
}

/// <summary>What <see cref="MembershipReasons.ReconcileAsync"/> did.</summary>
/// <param name="Adopted">Members that had no reason and were given their direct reason.</param>
/// <param name="Resynced">Members whose direct reason was re-synced from the row.</param>
/// <param name="Left">Members that do not match their reasons and hold a reason of another kind.</param>
internal sealed record MembershipReconciliation(int Adopted, IReadOnlyList<Guid> Resynced, IReadOnlyList<Guid> Left);
