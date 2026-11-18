using System.Text.Json;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;

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

/// <summary>
/// The one writer of the <c>auth_audit_log</c> rows that record a change to what a member can read:
/// the roles they hold, their direct permissions, their 24-hour history limit and the permissions a
/// role itself grants.
/// </summary>
/// <remarks>
/// Rows are staged on the context that stages the membership write, so one save commits both and a
/// write that fails leaves no row. A role or permission is one row per item:
/// <see cref="AuthAuditEventType.RoleRemoved"/> for each the change took away, then
/// <see cref="AuthAuditEventType.RoleAssigned"/> for each it gave, named in the details under
/// <c>role</c> or <c>permission</c>. A history limit that moved is one
/// <see cref="AuthAuditEventType.HistoryLimitChanged"/> row naming the limit after it under
/// <c>history_limit</c>. Every row's details carry <c>kind</c> = <c>tenant</c>, and <c>invite</c>
/// when an invite caused it. A change that leaves the set as it was stages nothing.
/// A role whose own permission set changed is one <see cref="AuthAuditEventType.RolePermissionsChanged"/>
/// row with no target subject, naming the role under <c>role_id</c> and <c>role</c> and the permissions
/// under <c>gained</c> and <c>lost</c>.
/// <para>
/// The target is the member's subject, except on the role row, which has none. The actor is the <see cref="MemberAccessSource"/> when the
/// writer passes one, else the scope's <see cref="IAuditContext"/> named as
/// <see cref="AuthAuditActor.From(IAuditContext)"/> names it, else the system when the scope has no
/// actor or is system-attributed. A scope with no caller at all, and no source, is recorded as
/// <c>anonymous</c>, never as a credential.
/// </para>
/// </remarks>
internal static class MemberAccessAudit
{
    private const string RoleKey = "role";
    private const string PermissionKey = "permission";
    private const string HistoryLimitKey = "history_limit";
    private const string InviteKey = "invite";
    private const string TwentyFourHours = "24_hours";
    private const string FullHistory = "full";
    private const string System = "system";
    private const string Anonymous = "anonymous";
    private const int CredentialLength = 64;

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

    /// <summary>
    /// Stages one <see cref="AuthAuditEventType.RolePermissionsChanged"/> row when the role's permission set
    /// moved, and none when it did not. The role is the target, so the row has no target subject.
    /// </summary>
    public static void StageRolePermissions(
        NocturneDbContext db, Guid tenantId, Guid roleId, string roleSlug,
        IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var gained = after.Except(before).Order(StringComparer.Ordinal).ToList();
        var lost = before.Except(after).Order(StringComparer.Ordinal).ToList();
        if (gained.Count == 0 && lost.Count == 0)
            return;

        Add(db, AuthAuditEventType.RolePermissionsChanged, tenantId, null, new Dictionary<string, object>
        {
            ["role_id"] = roleId.ToString(),
            [RoleKey] = roleSlug,
            ["gained"] = gained,
            ["lost"] = lost,
            ["kind"] = "tenant",
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
        var details = new Dictionary<string, object> { [key] = item, ["kind"] = "tenant" };
        if (source?.InviteId is { } inviteId)
            details[InviteKey] = inviteId.ToString();

        Add(db, eventType, tenantId, subjectId, details, source);
    }

    private static void Add(
        NocturneDbContext db, string eventType, Guid tenantId, Guid? targetSubjectId,
        Dictionary<string, object> details, MemberAccessSource? source)
    {
        var audit = db.AuditContext;
        var actor = ActorOf(audit, source);

        db.AuthAuditLog.Add(new AuthAuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            TargetSubjectId = targetSubjectId,
            ActorSubjectId = actor.SubjectId,
            ActorCredential = actor.Credential is { Length: > CredentialLength } credential ? credential[..CredentialLength] : actor.Credential,
            TenantId = tenantId,
            Success = true,
            IpAddress = audit?.IpAddress,
            UserAgent = audit?.UserAgent,
            TraceId = audit?.TraceId,
            DetailsJson = JsonSerializer.Serialize(details),
        });
    }

    private static AuthAuditActor ActorOf(IAuditContext? audit, MemberAccessSource? source)
    {
        if (source is not null)
            return new AuthAuditActor(source.ActorSubjectId, source.ActorSubjectId is null ? System : null);
        if (audit is null or { IsSystem: true })
            return new AuthAuditActor(null, audit?.AuthType ?? System);
        if (audit is { SubjectId: null, CredentialFingerprint: null, TokenId: null }
            && audit.AuthType is null or nameof(AuthType.None))
            return new AuthAuditActor(null, Anonymous);
        return AuthAuditActor.From(audit);
    }
}
