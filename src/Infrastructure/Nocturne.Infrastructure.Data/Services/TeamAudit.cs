using System.Globalization;
using Nocturne.Core.Contracts.Auth;
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
/// and an access link row names the link under <c>link</c>.
/// <para>
/// The two rows of a team's access to a site, given and stopped, carry the tenant as well, no target,
/// and <c>kind</c> = <c>team_access</c>, so the site's member history reads them. Their details hold the
/// team's name at the time under <c>name</c>, the role slugs under <c>roles</c>, the limit under
/// <c>history_limit</c> and the end under <c>access_ends_at</c> as <see cref="MemberAccessAudit"/> writes
/// them, the link under <c>link</c>, and for a stop how under <c>how</c>: <c>stopped</c> or <c>team_deleted</c>.
/// </para>
/// </remarks>
internal static class TeamAudit
{
    internal const string NameKey = "name";
    internal const string OldNameKey = "old_name";
    internal const string InviteKey = "invite";
    internal const string LinkKey = "link";
    internal const string RolesKey = "roles";
    internal const string StoppedHow = "stopped";
    internal const string TeamDeletedHow = "team_deleted";

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

    public static void StageAccessLinkCreated(NocturneDbContext db, Guid teamId, Guid linkId) =>
        Stage(db, AuthAuditEventType.TeamAccessLinkCreated, teamId, null, new() { [LinkKey] = linkId.ToString() });

    public static void StageAccessLinkRevoked(NocturneDbContext db, Guid teamId, Guid linkId) =>
        Stage(db, AuthAuditEventType.TeamAccessLinkRevoked, teamId, null, new() { [LinkKey] = linkId.ToString() });

    public static void StageAccessGiven(NocturneDbContext db, TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs) =>
        StageAccess(db, AuthAuditEventType.TeamAccessGiven, access, teamName, roleSlugs, how: null);

    /// <summary>Stages the stopped row, <paramref name="how"/> being <see cref="StoppedHow"/> or <see cref="TeamDeletedHow"/>.</summary>
    public static void StageAccessStopped(
        NocturneDbContext db, TeamTenantAccessEntity access, string teamName, IEnumerable<string> roleSlugs, string how) =>
        StageAccess(db, AuthAuditEventType.TeamAccessStopped, access, teamName, roleSlugs, how);

    private static void StageAccess(
        NocturneDbContext db, string eventType, TeamTenantAccessEntity access, string teamName,
        IEnumerable<string> roleSlugs, string? how)
    {
        var details = new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
            [NameKey] = teamName,
            [RolesKey] = roleSlugs.Order(StringComparer.Ordinal).ToList(),
            [MemberAccessAudit.HistoryLimitKey] = access.LimitTo24Hours ? MemberAccessAudit.TwentyFourHours : MemberAccessAudit.FullHistory,
            [MemberAccessAudit.AccessEndsAtKey] = access.AccessEndsAt is { } end
                ? DateTime.SpecifyKind(end, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                : null,
            [LinkKey] = access.CreatedFromLinkId?.ToString(),
        };
        if (how is not null)
            details[MemberAccessAudit.HowKey] = how;

        AuthAuditRows.Stage(db, eventType, access.TenantId, access.TeamId, targetSubjectId: null, details);
    }

    private static void Stage(
        NocturneDbContext db, string eventType, Guid teamId, Guid? targetSubjectId, Dictionary<string, object?> details)
    {
        details[AuthAuditKind.Key] = AuthAuditKind.Team;
        AuthAuditRows.Stage(db, eventType, tenantId: null, teamId, targetSubjectId, details);
    }
}
