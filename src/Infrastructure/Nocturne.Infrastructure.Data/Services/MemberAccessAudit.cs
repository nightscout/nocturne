using System.Globalization;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Who a membership change is attributed to in place of the scope's audit actor, and the invite that
/// caused it: a subject, or the system when <paramref name="ActorSubjectId"/> is null.
/// </summary>
/// <seealso cref="MembershipMadeBy"/>
internal sealed record MemberAccessSource(Guid? ActorSubjectId, Guid? InviteId = null)
{
    public static readonly MemberAccessSource System = new(ActorSubjectId: null);
}

/// <summary>The team whose access to the site a member's reason comes from, by the name it has when the row is written.</summary>
internal sealed record MemberAccessTeam(Guid Id, string Name);

/// <summary>
/// The one writer of the <c>auth_audit_log</c> rows that record a change to what a member can read:
/// the membership starting and ending, the roles they hold, their direct permissions, their 24-hour
/// history limit, the date their access ends and the permissions a role itself grants.
/// </summary>
/// <remarks>
/// Rows are staged on the context that stages the membership write, so one save commits both and a
/// write that fails leaves no row. A membership added is one <see cref="AuthAuditEventType.MemberJoined"/>
/// row before its other rows, and one removed is one <see cref="AuthAuditEventType.MemberRemoved"/> row
/// after them, both staged by <see cref="MembershipReasons"/> and naming how under <c>how</c>.
/// A role or permission is one row per item:
/// <see cref="AuthAuditEventType.RoleRemoved"/> for each the change took away, then
/// <see cref="AuthAuditEventType.RoleAssigned"/> for each it gave, named in the details under
/// <c>role</c> or <c>permission</c>. A history limit that moved is one
/// <see cref="AuthAuditEventType.HistoryLimitChanged"/> row naming the limit after it under
/// <c>history_limit</c>. An end date that moved is one <see cref="AuthAuditEventType.AccessEndChanged"/>
/// row naming the end after it under <c>access_ends_at</c>, as an ISO 8601 UTC instant or null when
/// the membership no longer ends; the date passing writes nothing. A guest link the member made that
/// an end date lowered is one <see cref="AuthAuditEventType.GuestLinkCapped"/> row, a device credential
/// one <see cref="AuthAuditEventType.DeviceCredentialCapped"/> row, and a guest link that removing the
/// member revoked is one <see cref="AuthAuditEventType.GuestLinkRevoked"/> row, each naming the grant
/// under <c>grant</c> (a cap also its new expiry under <c>expires_at</c>), with the member as the
/// target; a member invite that removing the member revoked is one
/// <see cref="AuthAuditEventType.MemberInviteRevoked"/> row naming the invite under <c>invite</c>, with
/// the member as the target. A device credential whose expiry was removed is one
/// <see cref="AuthAuditEventType.DeviceCredentialKept"/> row with its maker as the target. Every row's details carry <c>kind</c> = <c>tenant</c>, and <c>invite</c> when an invite
/// caused it. A change that leaves the set as it was stages nothing.
/// A role whose own permission set changed is one <see cref="AuthAuditEventType.RolePermissionsChanged"/>
/// row with no target subject, naming the role under <c>role_id</c> and <c>role</c> and the permissions
/// under <c>gained</c> and <c>lost</c>; a global role's row says <c>kind</c> = <c>global</c>.
/// <para>
/// The target is the member's subject, except on the role row, which has none. The actor is named by
/// <see cref="AuthAuditRows"/>.
/// </para>
/// </remarks>
internal static class MemberAccessAudit
{
    internal const string RoleKey = "role";
    internal const string GainedKey = "gained";
    internal const string LostKey = "lost";
    internal const string PermissionKey = "permission";
    internal const string HistoryLimitKey = "history_limit";
    internal const string AccessEndsAtKey = "access_ends_at";
    internal const string GrantKey = "grant";
    internal const string ExpiresAtKey = "expires_at";
    internal const string InviteKey = "invite";
    internal const string TwentyFourHours = "24_hours";
    internal const string FullHistory = "full";
    internal const string HowKey = "how";
    internal const string ByKey = "by";

    /// <summary>The <c>how</c> each way of joining is stored as.</summary>
    internal static readonly IReadOnlyDictionary<MembershipJoinHow, string> JoinedHow = new Dictionary<MembershipJoinHow, string>
    {
        [MembershipJoinHow.Invite] = "invite",
        [MembershipJoinHow.Request] = "request",
        [MembershipJoinHow.Direct] = "direct",
        [MembershipJoinHow.Setup] = "setup",
        [MembershipJoinHow.System] = "system",
        [MembershipJoinHow.Team] = "team",
    };

    /// <summary>The <c>how</c> each way of leaving is stored as.</summary>
    internal static readonly IReadOnlyDictionary<MembershipEndHow, string> RemovedHow = new Dictionary<MembershipEndHow, string>
    {
        [MembershipEndHow.Removed] = "removed",
        [MembershipEndHow.Left] = "left",
        [MembershipEndHow.AccountDeleted] = "account_deleted",
        [MembershipEndHow.Replaced] = "replaced",
        [MembershipEndHow.System] = "system",
        [MembershipEndHow.Team] = "team",
    };

    /// <summary>
    /// Stages the <see cref="AuthAuditEventType.MemberJoined"/> row of a membership or one of its reasons
    /// being added, naming <paramref name="how"/>, the invite and <paramref name="by"/> when there is one,
    /// and <paramref name="team"/> for a team's reason: its id on the row, its name in the details for
    /// when the team is gone.
    /// </summary>
    public static void StageJoined(
        NocturneDbContext db, Guid tenantId, Guid subjectId, MembershipJoinHow how, Guid? inviteId, Guid? by,
        MemberAccessTeam? team, MemberAccessSource? source)
    {
        var details = new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
            [HowKey] = JoinedHow[how],
        };
        if (inviteId is { } invite)
            details[InviteKey] = invite.ToString();
        if (by is { } responsible)
            details[ByKey] = responsible.ToString();
        if (team is not null)
            details[TeamAudit.NameKey] = team.Name;

        Add(db, AuthAuditEventType.MemberJoined, tenantId, team?.Id, subjectId, details, source);
    }

    /// <summary>
    /// Stages the <see cref="AuthAuditEventType.MemberRemoved"/> row of a membership or one of its reasons
    /// being removed, naming <paramref name="team"/> as <see cref="StageJoined"/> does. A removal
    /// whose actor is the member is recorded as <see cref="MembershipEndHow.Left"/>; a team's
    /// reason ending never is, since its writer names how and its actor itself.
    /// </summary>
    public static void StageRemoved(
        NocturneDbContext db, Guid tenantId, Guid subjectId, MembershipEndHow how, MemberAccessTeam? team,
        MemberAccessSource? source)
    {
        var actor = source is not null ? source.ActorSubjectId
            : db.AuditContext is { IsSystem: false } audit ? audit.SubjectId : null;
        if (how == MembershipEndHow.Removed && actor == subjectId)
            how = MembershipEndHow.Left;

        var details = new Dictionary<string, object?>
        {
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
            [HowKey] = RemovedHow[how],
        };
        if (team is not null)
            details[TeamAudit.NameKey] = team.Name;

        Add(db, AuthAuditEventType.MemberRemoved, tenantId, team?.Id, subjectId, details, source);
    }

    /// <summary>Stages a row for each role slug in <paramref name="removed"/>, then each in <paramref name="added"/>.</summary>
    public static void StageRoles(
        NocturneDbContext db, Guid tenantId, Guid subjectId, IEnumerable<string> added, IEnumerable<string> removed,
        MemberAccessSource? source = null) =>
        Stage(db, tenantId, subjectId, RoleKey, added, removed, source);

    /// <summary>Stages a row for each permission that left, then each that arrived, between the two sets.</summary>
    public static void StagePermissions(
        NocturneDbContext db, Guid tenantId, Guid subjectId,
        IReadOnlyCollection<string>? before, IReadOnlyCollection<string>? after, MemberAccessSource? source = null)
    {
        var held = before ?? [];
        var now = after ?? [];
        Stage(db, tenantId, subjectId, PermissionKey, now.Except(held), held.Except(now), source);
    }

    /// <summary>Stages one row when the history limit moved, and none when it did not.</summary>
    public static void StageHistoryLimit(
        NocturneDbContext db, Guid tenantId, Guid subjectId, bool before, bool after, MemberAccessSource? source = null)
    {
        if (before == after)
            return;

        Stage(db, AuthAuditEventType.HistoryLimitChanged, tenantId, subjectId, HistoryLimitKey,
            after ? TwentyFourHours : FullHistory, source);
    }

    /// <summary>Stages one row when the end date moved, and none when it did not.</summary>
    public static void StageAccessEnd(
        NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime? before, DateTime? after,
        MemberAccessSource? source = null)
    {
        if (before == after)
            return;

        var details = new Dictionary<string, object?>
        {
            [AccessEndsAtKey] = after is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null,
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
        };
        if (source?.InviteId is { } inviteId)
            details[InviteKey] = inviteId.ToString();

        Add(db, AuthAuditEventType.AccessEndChanged, tenantId, subjectId, details, source);
    }

    /// <summary>
    /// Stages one row for a grant the member made whose expiry their access end lowered to
    /// <paramref name="expiresAt"/>: <see cref="AuthAuditEventType.GuestLinkCapped"/> for a guest link,
    /// <see cref="AuthAuditEventType.DeviceCredentialCapped"/> for a device credential.
    /// </summary>
    public static void StageGrantCapped(
        NocturneDbContext db, string grantType, Guid tenantId, Guid memberSubjectId, Guid grantId, DateTime expiresAt) =>
        Add(db,
            grantType == OAuthGrantTypes.Guest ? AuthAuditEventType.GuestLinkCapped : AuthAuditEventType.DeviceCredentialCapped,
            tenantId, memberSubjectId, new Dictionary<string, object?>
            {
                [GrantKey] = grantId.ToString(),
                [ExpiresAtKey] = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                [AuthAuditKind.Key] = AuthAuditKind.Tenant,
            }, source: null);

    /// <summary>
    /// Stages one <see cref="AuthAuditEventType.DeviceCredentialKept"/> row for a device credential whose
    /// expiry was removed, with the member who made it as the target when it is known.
    /// </summary>
    public static void StageDeviceCredentialKept(
        NocturneDbContext db, Guid tenantId, Guid? makerSubjectId, Guid grantId) =>
        Add(db, AuthAuditEventType.DeviceCredentialKept, tenantId, makerSubjectId, new Dictionary<string, object?>
        {
            [GrantKey] = grantId.ToString(),
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
        }, source: null);

    /// <summary>
    /// Stages one <see cref="AuthAuditEventType.MemberInviteRevoked"/> row for a member invite the member
    /// made, revoked because the member was removed.
    /// </summary>
    public static void StageMemberInviteRevoked(
        NocturneDbContext db, Guid tenantId, Guid memberSubjectId, Guid inviteId) =>
        Add(db, AuthAuditEventType.MemberInviteRevoked, tenantId, memberSubjectId, new Dictionary<string, object?>
        {
            [InviteKey] = inviteId.ToString(),
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
        }, source: null);

    /// <summary>
    /// Stages one <see cref="AuthAuditEventType.GuestLinkRevoked"/> row for a guest link the member
    /// made, revoked because the member was removed.
    /// </summary>
    public static void StageGuestLinkRevoked(
        NocturneDbContext db, Guid tenantId, Guid memberSubjectId, Guid grantId) =>
        Add(db, AuthAuditEventType.GuestLinkRevoked, tenantId, memberSubjectId, new Dictionary<string, object?>
        {
            [GrantKey] = grantId.ToString(),
            [AuthAuditKind.Key] = AuthAuditKind.Tenant,
        }, source: null);

    /// <summary>
    /// Stages one <see cref="AuthAuditEventType.RolePermissionsChanged"/> row when the role's permission set
    /// moved, and none when it did not. The role is the target, so the row has no target subject.
    /// </summary>
    public static void StageRolePermissions(
        NocturneDbContext db, Guid tenantId, Guid roleId, string roleSlug,
        IReadOnlyCollection<string> before, IReadOnlyCollection<string> after) =>
        StageRolePermissions(db, tenantId, AuthAuditKind.Tenant, roleId, roleSlug, before, after);

    /// <summary>
    /// <see cref="StageRolePermissions(NocturneDbContext, Guid, Guid, string, IReadOnlyCollection{string}, IReadOnlyCollection{string})"/>
    /// for a global role, which belongs to no tenant: the row's <c>kind</c> is <see cref="AuthAuditKind.Global"/>
    /// and it carries no tenant id, whatever tenant the edit was made under.
    /// </summary>
    public static void StageGlobalRolePermissions(
        NocturneDbContext db, Guid roleId, string roleName,
        IReadOnlyCollection<string> before, IReadOnlyCollection<string> after) =>
        StageRolePermissions(db, null, AuthAuditKind.Global, roleId, roleName, before, after);

    private static void StageRolePermissions(
        NocturneDbContext db, Guid? tenantId, string kind, Guid roleId, string role,
        IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var gained = after.Except(before).Order(StringComparer.Ordinal).ToList();
        var lost = before.Except(after).Order(StringComparer.Ordinal).ToList();
        if (gained.Count == 0 && lost.Count == 0)
            return;

        Add(db, AuthAuditEventType.RolePermissionsChanged, tenantId, null, new Dictionary<string, object?>
        {
            ["role_id"] = roleId.ToString(),
            [RoleKey] = role,
            [GainedKey] = gained,
            [LostKey] = lost,
            [AuthAuditKind.Key] = kind,
        }, source: null);
    }

    private static void Stage(
        NocturneDbContext db, Guid tenantId, Guid subjectId, string key, IEnumerable<string> added,
        IEnumerable<string> removed, MemberAccessSource? source)
    {
        foreach (var item in removed)
            Stage(db, AuthAuditEventType.RoleRemoved, tenantId, subjectId, key, item, source);
        foreach (var item in added)
            Stage(db, AuthAuditEventType.RoleAssigned, tenantId, subjectId, key, item, source);
    }

    private static void Stage(
        NocturneDbContext db, string eventType, Guid tenantId, Guid subjectId, string key, string item,
        MemberAccessSource? source)
    {
        var details = new Dictionary<string, object?> { [key] = item, [AuthAuditKind.Key] = AuthAuditKind.Tenant };
        if (source?.InviteId is { } inviteId)
            details[InviteKey] = inviteId.ToString();

        Add(db, eventType, tenantId, subjectId, details, source);
    }

    private static void Add(
        NocturneDbContext db, string eventType, Guid? tenantId, Guid? targetSubjectId,
        Dictionary<string, object?> details, MemberAccessSource? source) =>
        Add(db, eventType, tenantId, teamId: null, targetSubjectId, details, source);

    private static void Add(
        NocturneDbContext db, string eventType, Guid? tenantId, Guid? teamId, Guid? targetSubjectId,
        Dictionary<string, object?> details, MemberAccessSource? source) =>
        AuthAuditRows.Stage(db, eventType, tenantId, teamId, targetSubjectId, details, source);
}
