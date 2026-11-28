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
/// history reads it. A change of the rule has no target and holds the new rule under <c>staff_rule</c>;
/// rows written before the rule had three values hold a boolean under <c>team_manages_staff</c>. An owner's
/// stop of one person through any team, set or removed, is a <c>team_access</c> row with no team that
/// targets that person.
/// </para>
/// <para>
/// A group made, renamed or deleted, a site or a staff member put in a group or taken out, and a change of
/// which people the staff reach are <c>team</c> rows naming the group under <c>group</c>, a site under
/// <c>site</c>, a staff member as the target, a name under <c>name</c> and <c>old_name</c>, and the value
/// under <c>staff_reach</c>. Each of them that changes who can see a site is also a <c>team_access</c> row at
/// that site, naming the team's name under <c>name</c>, targeting a staff member put in or taken out, and
/// holding <c>staff_reach</c> for a change of it. Those rows are attributed to the system, as the team
/// reasons they change are (<see cref="Repositories.TeamReasonChanges"/>), so the site never reads which manager made it,
/// and name no group, so no site reads another site's group.
/// </para>
/// <para>
/// A flag rule added, changed or deleted is a <c>team</c> row holding the rule under <c>flag_rule</c>: its id, dataset,
/// field, days, comparison, value and level as the table stores them. A change holds the rule before it under
/// <c>old_flag_rule</c>. No site has a row for it, since a rule changes nobody's access.
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
    internal const string StaffRuleKey = "staff_rule";
    internal const string StaffReachKey = "staff_reach";
    internal const string GroupKey = "group";
    internal const string SiteKey = "site";
    internal const string FlagRuleKey = "flag_rule";
    internal const string OldFlagRuleKey = "old_flag_rule";

    /// <summary>The key of the rule's rows written before it had three values: true for <see cref="TeamStaffRule.TeamDecides"/>, false for <see cref="TeamStaffRule.OwnerApproves"/>.</summary>
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

    /// <summary>A staff member joined, by the staff invite they accepted, or by none when the instance provisioned them.</summary>
    public static void StageStaffAdded(NocturneDbContext db, Guid teamId, Guid staffSubjectId, Guid? inviteId) =>
        Stage(db, AuthAuditEventType.TeamStaffAdded, teamId, staffSubjectId,
            inviteId is { } id ? new() { [InviteKey] = id.ToString() } : []);

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
            [StaffRuleKey] = access.StaffRule,
        });

    /// <summary>
    /// Stages an owner's stop of <paramref name="subjectId"/> at <paramref name="tenantId"/> through any team,
    /// set or removed: <see cref="AuthAuditEventType.TeamStaffBlocked"/> or <see cref="AuthAuditEventType.TeamStaffUnblocked"/>.
    /// </summary>
    public static void StageStaffBlock(NocturneDbContext db, string eventType, Guid tenantId, Guid subjectId) =>
        AuthAuditRows.Stage(db, eventType, tenantId, teamId: null, subjectId, new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
        });

    public static void StageGroupCreated(NocturneDbContext db, Guid teamId, Guid groupId, string name) =>
        Stage(db, AuthAuditEventType.TeamGroupCreated, teamId, null, new() { [GroupKey] = groupId.ToString(), [NameKey] = name });

    public static void StageGroupRenamed(NocturneDbContext db, Guid teamId, Guid groupId, string oldName, string name) =>
        Stage(db, AuthAuditEventType.TeamGroupRenamed, teamId, null,
            new() { [GroupKey] = groupId.ToString(), [OldNameKey] = oldName, [NameKey] = name });

    /// <summary>Stages a group's deletion, and a row at each of <paramref name="sites"/>, the sites whose access it changes.</summary>
    public static void StageGroupDeleted(
        NocturneDbContext db, Guid teamId, string teamName, Guid groupId, string groupName, IEnumerable<Guid> sites)
    {
        Stage(db, AuthAuditEventType.TeamGroupDeleted, teamId, null, new() { [GroupKey] = groupId.ToString(), [NameKey] = groupName });
        StageAtSites(db, AuthAuditEventType.TeamGroupDeleted, sites, teamId, teamName, targetSubjectId: null, staffReach: null);
    }

    /// <summary>
    /// Stages a site put in a group or taken out, <see cref="AuthAuditEventType.TeamGroupPersonAdded"/> or
    /// <see cref="AuthAuditEventType.TeamGroupPersonRemoved"/>, and a row at the site when <paramref name="atSite"/>.
    /// </summary>
    public static void StageGroupPerson(
        NocturneDbContext db, string eventType, Guid teamId, string teamName, Guid groupId, Guid tenantId, bool atSite)
    {
        Stage(db, eventType, teamId, null, new() { [GroupKey] = groupId.ToString(), [SiteKey] = tenantId.ToString() });
        StageAtSites(db, eventType, atSite ? [tenantId] : [], teamId, teamName, targetSubjectId: null, staffReach: null);
    }

    /// <summary>
    /// Stages a staff member put in a group or taken out, <see cref="AuthAuditEventType.TeamGroupStaffAdded"/> or
    /// <see cref="AuthAuditEventType.TeamGroupStaffRemoved"/>, and a row at each of <paramref name="sites"/>.
    /// </summary>
    public static void StageGroupStaff(
        NocturneDbContext db, string eventType, Guid teamId, string teamName, Guid groupId, Guid staffSubjectId,
        IEnumerable<Guid> sites)
    {
        Stage(db, eventType, teamId, staffSubjectId, new() { [GroupKey] = groupId.ToString() });
        StageAtSites(db, eventType, sites, teamId, teamName, staffSubjectId, staffReach: null);
    }

    /// <summary>Stages a change of which people the team's staff reach to <paramref name="staffReach"/>, and a row at each of <paramref name="sites"/>.</summary>
    public static void StageStaffReachChanged(
        NocturneDbContext db, Guid teamId, string teamName, string staffReach, IEnumerable<Guid> sites)
    {
        Stage(db, AuthAuditEventType.TeamStaffReachChanged, teamId, null, new() { [StaffReachKey] = staffReach });
        StageAtSites(db, AuthAuditEventType.TeamStaffReachChanged, sites, teamId, teamName, targetSubjectId: null, staffReach);
    }

    public static void StageFlagRuleCreated(NocturneDbContext db, Guid teamId, FlagRuleEntity rule) =>
        Stage(db, AuthAuditEventType.TeamFlagRuleCreated, teamId, null, new() { [FlagRuleKey] = FlagRuleDetails(rule) });

    /// <param name="before">The rule as <see cref="FlagRuleDetails"/> read it before the change.</param>
    public static void StageFlagRuleChanged(
        NocturneDbContext db, Guid teamId, IReadOnlyDictionary<string, object?> before, FlagRuleEntity rule) =>
        Stage(db, AuthAuditEventType.TeamFlagRuleChanged, teamId, null,
            new() { [OldFlagRuleKey] = before, [FlagRuleKey] = FlagRuleDetails(rule) });

    public static void StageFlagRuleDeleted(NocturneDbContext db, Guid teamId, FlagRuleEntity rule) =>
        Stage(db, AuthAuditEventType.TeamFlagRuleDeleted, teamId, null, new() { [FlagRuleKey] = FlagRuleDetails(rule) });

    /// <summary>What a flag rule row of the team's history holds of the rule.</summary>
    public static IReadOnlyDictionary<string, object?> FlagRuleDetails(FlagRuleEntity rule) => new Dictionary<string, object?>
    {
        ["id"] = rule.Id.ToString(),
        ["dataset"] = rule.Dataset,
        ["field"] = rule.Field,
        ["days"] = rule.Days,
        ["comparison"] = rule.Comparison,
        ["value"] = rule.Value,
        ["level"] = rule.Level,
    };

    private static void StageAtSites(
        NocturneDbContext db, string eventType, IEnumerable<Guid> sites, Guid teamId, string teamName, Guid? targetSubjectId,
        string? staffReach)
    {
        foreach (var site in sites.Distinct().Order())
        {
            var details = new Dictionary<string, object?>
            {
                [AuthAuditKind.Key] = AuthAuditKind.TeamAccess,
                [NameKey] = teamName,
            };
            if (staffReach is not null)
                details[StaffReachKey] = staffReach;
            AuthAuditRows.Stage(db, eventType, site, teamId, targetSubjectId, details, MemberAccessSource.System);
        }
    }

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
