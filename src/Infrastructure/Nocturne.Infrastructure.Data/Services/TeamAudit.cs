using Nocturne.Core.Contracts.Auth;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The one writer of the <c>auth_audit_log</c> rows that record a change to a team, staged through
/// <see cref="AuthAuditRows"/> on the context of the write.
/// </summary>
/// <remarks>
/// Every row carries the team in <c>team_id</c>, no tenant, and <c>kind</c> = <c>team</c> in its
/// details, so a tenant's member history never reads it. A manager row targets the manager's subject;
/// a rename's details hold the old and the new name; an invite row names the invite under <c>invite</c>.
/// </remarks>
internal static class TeamAudit
{
    internal const string TeamKind = "team";
    internal const string NameKey = "name";
    internal const string OldNameKey = "old_name";
    internal const string InviteKey = "invite";

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

    private static void Stage(
        NocturneDbContext db, string eventType, Guid teamId, Guid? targetSubjectId, Dictionary<string, object?> details)
    {
        details[AuthAuditRows.KindKey] = TeamKind;
        AuthAuditRows.Stage(db, eventType, tenantId: null, teamId, targetSubjectId, details);
    }
}
