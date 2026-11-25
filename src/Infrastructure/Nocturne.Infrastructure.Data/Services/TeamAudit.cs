using System.Globalization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The one writer of the <c>auth_audit_log</c> rows that record a change to a team, staged through
/// <see cref="AuthAuditRows"/> on the context of the write.
/// </summary>
/// <remarks>
/// Every row carries the team in <c>team_id</c>, no tenant, and <c>kind</c> = <c>team</c> in its
/// details, so a tenant's member history never reads it. A manager row targets the manager's subject;
/// a rename's details hold the old and the new name; an invite row names the invite under <c>invite</c>,
/// and an access link row names the link under <c>link</c>. A staff row targets the staff member's subject;
/// a staff invite row names the invite under <c>invite</c>, and a staff member added the invite they
/// joined by. A staff member changed holds the ceiling under <c>old_ceiling</c> and <c>ceiling</c>, the
/// limit under <c>old_history_limit</c> and <c>history_limit</c>, and the end under <c>old_ends_at</c>
/// and <c>ends_at</c>, as <see cref="MemberAccessAudit"/> writes them. A staff member removed names how
/// under <c>how</c>: <c>left</c> when the actor is the target, else <c>removed</c>.
/// <para>
/// The two rows of a team's access to a site, given and stopped, carry the tenant as well, no target,
/// and <c>kind</c> = <c>team_access</c>, so the site's member history reads them. Their details hold the
/// team's name at the time under <c>name</c>, the role slugs under <c>roles</c>, the limit under
/// <c>history_limit</c> and the end under <c>access_ends_at</c> as <see cref="MemberAccessAudit"/> writes
/// them, the link under <c>link</c>, and for a stop how under <c>how</c>: <c>stopped</c> or <c>team_deleted</c>.
/// An owner's change of what the access gives is a third such row, holding what it gives after the change
/// under those keys and what it gave before under <c>old_roles</c>, <c>old_history_limit</c> and
/// <c>old_access_ends_at</c>.
/// </para>
/// <para>
/// An owner's decision about the team's staff at a site is a <c>team_access</c> row too, naming the team's
/// name under <c>name</c>. A staff member approved or declined targets that staff member, so their own
/// history reads it. A change of the switch has no target and holds the new value under
/// <c>team_manages_staff</c>.
/// </para>
/// </remarks>
internal static class TeamAudit
{
    internal const string NameKey = "name";
    internal const string OldNameKey = "old_name";
    internal const string InviteKey = "invite";
    internal const string LinkKey = "link";
    internal const string RolesKey = "roles";
    internal const string OldRolesKey = "old_roles";
    internal const string OldAccessEndsAtKey = "old_access_ends_at";
    internal const string OldCeilingKey = "old_ceiling";
    internal const string CeilingKey = "ceiling";
    internal const string OldHistoryLimitKey = "old_history_limit";
    internal const string OldEndsAtKey = "old_ends_at";
    internal const string EndsAtKey = "ends_at";
    internal const string StoppedHow = "stopped";
    internal const string TeamDeletedHow = "team_deleted";
    internal const string TeamManagesStaffKey = "team_manages_staff";

    public static void StageCreated(NocturneDbContext db, Guid teamId, string name) =>
        Stage(db, AuthAuditEventType.TeamCreated, teamId, null, new() { [NameKey] = name });

    public static void StageRenamed(NocturneDbContext db, Guid teamId, string oldName, string name) =>
        Stage(db, AuthAuditEventType.TeamRenamed, teamId, null, new() { [OldNameKey] = oldName, [NameKey] = name });

    public static void StageDeleted(NocturneDbContext db, Guid teamId, string name) =>
        Stage(db, AuthAuditEventType.TeamDeleted, teamId, null, new() { [NameKey] = name });

    /// <param name="inviteId">The invite the manager accepted, or null for the team's creator.</param>
    public static void StageManagerAdded(NocturneDbContext db, Guid teamId, Guid managerSubjectId, Guid? inviteId) =>
        Stage(db, AuthAuditEventType.TeamManagerAdded, teamId, managerSubjectId,
            inviteId is { } id ? new() { [InviteKey] = id.ToString() } : []);

    public static void StageManagerRemoved(NocturneDbContext db, Guid teamId, Guid managerSubjectId) =>
        Stage(db, AuthAuditEventType.TeamManagerRemoved, teamId, managerSubjectId, []);

    public static void StageInviteCreated(NocturneDbContext db, Guid teamId, Guid inviteId) =>
        Stage(db, AuthAuditEventType.TeamInviteCreated, teamId, null, new() { [InviteKey] = inviteId.ToString() });

    public static void StageInviteRevoked(NocturneDbContext db, Guid teamId, Guid inviteId) =>
        Stage(db, AuthAuditEventType.TeamInviteRevoked, teamId, null, new() { [InviteKey] = inviteId.ToString() });

    public static void StageStaffInviteCreated(NocturneDbContext db, Guid teamId, Guid inviteId) =>
        Stage(db, AuthAuditEventType.TeamStaffInviteCreated, teamId, null, new() { [InviteKey] = inviteId.ToString() });

    public static void StageStaffInviteRevoked(NocturneDbContext db, Guid teamId, Guid inviteId) =>
        Stage(db, AuthAuditEventType.TeamStaffInviteRevoked, teamId, null, new() { [InviteKey] = inviteId.ToString() });

    public static void StageStaffAdded(NocturneDbContext db, Guid teamId, Guid staffSubjectId, Guid inviteId) =>
        Stage(db, AuthAuditEventType.TeamStaffAdded, teamId, staffSubjectId, new() { [InviteKey] = inviteId.ToString() });

    public static void StageStaffChanged(
        NocturneDbContext db, Guid teamId, Guid staffSubjectId, TeamStaffNarrowing before, TeamStaffNarrowing after) =>
        Stage(db, AuthAuditEventType.TeamStaffChanged, teamId, staffSubjectId, new()
        {
            [OldCeilingKey] = before.RoleCeilingSlug,
            [CeilingKey] = after.RoleCeilingSlug,
            [OldHistoryLimitKey] = HistoryLimit(before),
            [MemberAccessAudit.HistoryLimitKey] = HistoryLimit(after),
            [OldEndsAtKey] = Instant(before.EndsAt),
            [EndsAtKey] = Instant(after.EndsAt),
        });

    /// <summary>
    /// Stages the removal of a staff member, <c>left</c> when the scope's audit actor is the staff member
    /// and <c>removed</c> otherwise, as <see cref="MemberAccessAudit.StageRemoved"/> decides for a member.
    /// </summary>
    public static void StageStaffRemoved(NocturneDbContext db, Guid teamId, Guid staffSubjectId)
    {
        var actor = db.AuditContext is { IsSystem: false } audit ? audit.SubjectId : null;
        var how = actor == staffSubjectId ? MembershipEndHow.Left : MembershipEndHow.Removed;
        Stage(db, AuthAuditEventType.TeamStaffRemoved, teamId, staffSubjectId,
            new() { [MemberAccessAudit.HowKey] = MemberAccessAudit.RemovedHow[how] });
    }

    public static void StageAccessLinkCreated(NocturneDbContext db, Guid teamId, Guid linkId) =>
        Stage(db, AuthAuditEventType.TeamAccessLinkCreated, teamId, null, new() { [LinkKey] = linkId.ToString() });

    public static void StageAccessLinkRevoked(NocturneDbContext db, Guid teamId, Guid linkId) =>
        Stage(db, AuthAuditEventType.TeamAccessLinkRevoked, teamId, null, new() { [LinkKey] = linkId.ToString() });

    public static void StageAccessGiven(NocturneDbContext db, TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs) =>
        StageAccess(db, AuthAuditEventType.TeamAccessGiven, access, teamName, roleSlugs, how: null);

    /// <summary>Stages an owner's change of what <paramref name="access"/> gives, from <paramref name="before"/> to what it gives now.</summary>
    public static void StageAccessChanged(
        NocturneDbContext db, TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs,
        (IReadOnlyList<string> Roles, bool LimitTo24Hours, DateTime? AccessEndsAt) before)
    {
        var details = AccessDetails(access, teamName, roleSlugs);
        details[OldRolesKey] = before.Roles.Order(StringComparer.Ordinal).ToList();
        details[OldHistoryLimitKey] = before.LimitTo24Hours ? MemberAccessAudit.TwentyFourHours : MemberAccessAudit.FullHistory;
        details[OldAccessEndsAtKey] = Instant(before.AccessEndsAt);
        AuthAuditRows.Stage(db, AuthAuditEventType.TeamAccessChanged, access.TenantId, access.TeamId, targetSubjectId: null, details);
    }

    /// <summary>Stages the stopped row, <paramref name="how"/> being <see cref="StoppedHow"/> or <see cref="TeamDeletedHow"/>.</summary>
    public static void StageAccessStopped(
        NocturneDbContext db, TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs, string how) =>
        StageAccess(db, AuthAuditEventType.TeamAccessStopped, access, teamName, roleSlugs, how);

    /// <summary>
    /// Stages an owner's decision about <paramref name="staffSubjectId"/> at the site of
    /// <paramref name="access"/>: <see cref="AuthAuditEventType.TeamStaffApproved"/> or
    /// <see cref="AuthAuditEventType.TeamStaffDeclined"/>.
    /// </summary>
    public static void StageStaffDecided(
        NocturneDbContext db, string eventType, TeamTenantAccessEntity access, string teamName, Guid staffSubjectId) =>
        AuthAuditRows.Stage(db, eventType, access.TenantId, access.TeamId, staffSubjectId, new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
            [NameKey] = teamName,
        });

    public static void StageStaffRuleChanged(NocturneDbContext db, TeamTenantAccessEntity access, string teamName) =>
        AuthAuditRows.Stage(db, AuthAuditEventType.TeamStaffRuleChanged, access.TenantId, access.TeamId, targetSubjectId: null, new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
            [NameKey] = teamName,
            [TeamManagesStaffKey] = access.TeamManagesStaff,
        });

    private static string HistoryLimit(TeamStaffNarrowing narrowing) =>
        narrowing.LimitTo24Hours ? MemberAccessAudit.TwentyFourHours : MemberAccessAudit.FullHistory;

    private static string? Instant(DateTime? instant) =>
        instant is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null;

    private static void StageAccess(
        NocturneDbContext db, string eventType, TeamTenantAccessEntity access, string teamName,
        IEnumerable<string> roleSlugs, string? how)
    {
        var details = AccessDetails(access, teamName, roleSlugs);
        if (how is not null)
            details[MemberAccessAudit.HowKey] = how;

        AuthAuditRows.Stage(db, eventType, access.TenantId, access.TeamId, targetSubjectId: null, details);
    }

    private static Dictionary<string, object?> AccessDetails(TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs) =>
        new()
        {
            [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
            [NameKey] = teamName,
            [RolesKey] = roleSlugs.Order(StringComparer.Ordinal).ToList(),
            [MemberAccessAudit.HistoryLimitKey] = access.LimitTo24Hours ? MemberAccessAudit.TwentyFourHours : MemberAccessAudit.FullHistory,
            [MemberAccessAudit.AccessEndsAtKey] = Instant(access.AccessEndsAt),
            [LinkKey] = access.CreatedFromLinkId?.ToString(),
        };

    private static void Stage(
        NocturneDbContext db, string eventType, Guid teamId, Guid? targetSubjectId, Dictionary<string, object?> details)
    {
        details[AuthAuditKind.Key] = AuthAuditKind.Team;
        AuthAuditRows.Stage(db, eventType, tenantId: null, teamId, targetSubjectId, details);
    }
}
