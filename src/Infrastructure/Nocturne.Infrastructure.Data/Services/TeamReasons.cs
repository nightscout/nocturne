using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The team reasons a subject's membership of a site holds when it agrees with the team's rows: one for
/// each access to the site whose team has the subject as staff, approved there by its
/// <c>team_site_staff</c> row, and whose staff member's ceiling is a seed role this release knows. Each
/// gives the access's roles capped at that ceiling (<see cref="GrantOf"/>), the 24-hour limit when the
/// access or the staff member has it, and the earlier of their two ends.
/// </summary>
/// <remarks>
/// A ceiling no seed role has gives nothing, so a staff member whose ceiling cannot be read reaches
/// nothing rather than everything the access holds. A ceiling that shares nothing with the access's roles
/// gives no reason either: a membership that reaches nothing would still list the staff member as having
/// access. A team reason never gives the owner role: an access holds data roles only.
/// <para>
/// An owner of the site is never staff there, so a subject whose direct reason holds the owner role and is
/// in force holds no team reason at the site. Their access stands on the direct reason alone, and stepping
/// down or losing the owner role leaves them what the team's rows give. A direct reason that has lapsed is
/// no owner.
/// </para>
/// </remarks>
internal static class TeamReasons
{
    /// <summary>A role a team's access gives, with its permissions as they are now.</summary>
    internal sealed record AccessRole(Guid Id, IReadOnlyList<string> Permissions);

    /// <summary>
    /// What the team reasons of <paramref name="subjectId"/> on <paramref name="tenantId"/> give when they
    /// agree with the team's rows, keyed by access id, ends that have passed included; none for an owner of
    /// the site at <paramref name="now"/>. Reads the rows of that site only, on a context pinned to it or to
    /// none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, MembershipGrant>> DesiredAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        if (await OwnsAsync(db, tenantId, subjectId, now, ct))
            return new Dictionary<Guid, MembershipGrant>();

        var rows = await db.TeamSiteStaff.AsNoTracking()
            .Where(s => s.Status == TeamSiteStaffStatus.Approved
                && s.TeamTenantAccess!.TenantId == tenantId
                && s.TeamMember!.SubjectId == subjectId
                && s.TeamMember.Kind == TeamMemberKind.Staff)
            .Select(s => new
            {
                AccessId = s.TeamTenantAccessId,
                AccessLimit = s.TeamTenantAccess!.LimitTo24Hours,
                AccessEndsAt = s.TeamTenantAccess.AccessEndsAt,
                s.TeamMember!.RoleCeilingSlug,
                StaffLimit = s.TeamMember.LimitTo24Hours,
                StaffEndsAt = s.TeamMember.EndsAt,
                Roles = s.TeamTenantAccess.AccessRoles
                    .Select(r => new { r.TenantRoleId, r.TenantRole!.Permissions })
                    .ToList(),
            })
            .ToListAsync(ct);

        var desired = new Dictionary<Guid, MembershipGrant>();
        foreach (var row in rows)
        {
            var grant = GrantOf(
                row.Roles.Select(r => new AccessRole(r.TenantRoleId, r.Permissions)).ToList(),
                row.RoleCeilingSlug,
                row.AccessLimit || row.StaffLimit,
                Earlier(row.AccessEndsAt, row.StaffEndsAt));
            if (grant is not null)
                desired[row.AccessId] = grant;
        }

        return desired;
    }

    /// <summary>
    /// Whether the subject's direct reason on the site holds the owner role and is in force at
    /// <paramref name="now"/>, as <see cref="MembershipReasons"/> brings it into line with the row before a
    /// change: a member who holds no team reason has their row for a direct reason, since a row with no
    /// reason is adopted and a lone direct reason that differs from the row is re-synced from it.
    /// </summary>
    private static Task<bool> OwnsAsync(NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        db.TenantMembers.AsNoTracking().AnyAsync(m =>
            m.TenantId == tenantId
            && m.SubjectId == subjectId
            && (m.Reasons.Any(r => r.Kind == MembershipReasonKind.Team)
                ? m.Reasons.Any(r =>
                    r.Kind == MembershipReasonKind.Direct
                    && (r.AccessEndsAt == null || r.AccessEndsAt > now)
                    && r.ReasonRoles.Any(rr => rr.TenantRole.Slug == RoleSeeds.Owner))
                : (m.AccessEndsAt == null || m.AccessEndsAt > now)
                    && m.MemberRoles.Any(mr => mr.TenantRole.Slug == RoleSeeds.Owner)),
            ct);

    /// <summary>
    /// What a team access holding <paramref name="roles"/> gives a staff member whose ceiling is
    /// <paramref name="ceilingSlug"/>: with no ceiling, every role; otherwise each role whose permissions the
    /// ceiling's seed role holds, and of each other role, as direct permissions, what it and the seed both
    /// grant (<see cref="Scope.Common"/>), in ordinal order and null when there is none. Null when no seed
    /// role has the slug, or when the ceiling and the roles share nothing.
    /// </summary>
    public static MembershipGrant? GrantOf(
        IReadOnlyCollection<AccessRole> roles, string? ceilingSlug, bool limitTo24Hours, DateTime? accessEndsAt)
    {
        if (ceilingSlug is null)
            return new MembershipGrant(roles.Select(r => r.Id).ToList(), null, limitTo24Hours, accessEndsAt);
        if (!RoleSeeds.Permissions.TryGetValue(ceilingSlug, out var seed))
            return null;

        var ceiling = seed.ToHashSet(StringComparer.Ordinal);
        var roleIds = new List<Guid>();
        var partial = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (Scope.ValidateGrant(role.Permissions, ceiling) is null)
                roleIds.Add(role.Id);
            else
                partial.UnionWith(Scope.Common(role.Permissions.ToHashSet(StringComparer.Ordinal), ceiling));
        }

        return roleIds.Count == 0 && partial.Count == 0
            ? null
            : new MembershipGrant(roleIds, partial.Count == 0 ? null : partial.ToList(), limitTo24Hours, accessEndsAt);
    }

    /// <summary>Whether two grants give the same, as a membership's row compares them.</summary>
    public static bool Same(MembershipGrant first, MembershipGrant second) =>
        first.RoleIds.ToHashSet().SetEquals(second.RoleIds)
        && (first.DirectPermissions is null
            ? second.DirectPermissions is null
            : second.DirectPermissions is not null && first.DirectPermissions.SequenceEqual(second.DirectPermissions, StringComparer.Ordinal))
        && first.LimitTo24Hours == second.LimitTo24Hours
        && first.AccessEndsAt == second.AccessEndsAt;

    private static DateTime? Earlier(DateTime? first, DateTime? second) =>
        first is { } a && second is { } b ? (a < b ? a : b) : first ?? second;
}
