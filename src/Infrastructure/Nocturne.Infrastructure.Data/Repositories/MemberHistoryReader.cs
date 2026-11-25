using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IMemberHistoryReader"/>
/// <remarks>
/// <c>auth_audit_log</c> is not tenant-scoped, so the <c>tenant_id</c> predicate is the isolation, and
/// the context is pinned to that tenant so the invite lookup reads under its row level security.
/// <see cref="MemberAccessAudit"/> and <c>SubjectService</c> write the same event types; the writer's
/// rows carry <see cref="AuthAuditKind.Tenant"/>. A row with no details is kept and reads as a change
/// whose detail is unknown. A global role row (<see cref="AuthAuditKind.Global"/>) carries no tenant id, so
/// the <c>tenant_id</c> predicate drops it whether or not its details survive; one whose details were
/// cleared before it lost its tenant id reads as a change whose detail is unknown. An
/// <c>access_end_changed</c> row is told apart by its
/// <c>access_ends_at</c> key: a string is the new end, a null is a cleared end, and no key is a row
/// whose detail is unknown.
/// <para>
/// A team's access to the site given, changed or stopped is a <see cref="TeamAudit"/> row with
/// <c>kind</c> = <c>team_access</c> and no target, so it never appears in a page narrowed to a member.
/// It names the team by its name now, or the name the row recorded once the team is gone. An owner's
/// approval or decline of a staff member is a <c>team_access</c> row that targets the staff member, so it
/// appears in their page too; a change of the switch has no target.
/// </para>
/// <para>
/// A change an invite made is <see cref="MemberHistoryEntry.InRecordedJoin"/> when a
/// <see cref="AuthAuditEventType.MemberJoined"/> row of the same commit names the same member and
/// invite; a join older than that row has none, and its changes keep reading as the join itself.
/// </para>
/// </remarks>
public class MemberHistoryReader(INocturneDbContextFactory contexts) : IMemberHistoryReader
{
    private static readonly string[] EventTypes =
    [
        AuthAuditEventType.RoleAssigned,
        AuthAuditEventType.RoleRemoved,
        AuthAuditEventType.HistoryLimitChanged,
        AuthAuditEventType.AccessEndChanged,
        AuthAuditEventType.RolePermissionsChanged,
        AuthAuditEventType.MemberJoined,
        AuthAuditEventType.MemberRemoved,
        AuthAuditEventType.TeamAccessGiven,
        AuthAuditEventType.TeamAccessStopped,
        AuthAuditEventType.TeamAccessChanged,
        AuthAuditEventType.TeamStaffApproved,
        AuthAuditEventType.TeamStaffDeclined,
        AuthAuditEventType.TeamStaffRuleChanged,
    ];

    private const string MemberChangeRows =
        $"SELECT * FROM auth_audit_log WHERE details IS NULL OR details ->> '{AuthAuditKind.Key}' IN ('{AuthAuditKind.Tenant}', '{AuthAuditKind.TeamAccess}')";

    public async Task<MemberHistoryPage> GetAsync(
        Guid tenantId, Guid? memberSubjectId, int limit, int offset, DateTime now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);

        var rows = db.AuthAuditLog
            .FromSqlRaw(MemberChangeRows)
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && EventTypes.Contains(a.EventType));
        if (memberSubjectId is { } member)
            rows = rows.Where(a => a.TargetSubjectId == member);

        var total = await rows.CountAsync(ct);
        var page = await rows
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var parsed = page.Select(row => (Row: row, Details: Details.Of(row.DetailsJson))).ToList();

        var subjectIds = parsed
            .SelectMany(p => new[] { p.Row.TargetSubjectId, p.Row.ActorSubjectId, p.Details.By })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var names = subjectIds.Count == 0
            ? []
            : await db.Subjects.AsNoTracking()
                .Where(s => subjectIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var inviteIds = parsed.Select(p => p.Details.InviteId).OfType<Guid>().Distinct().ToList();
        var labels = inviteIds.Count == 0
            ? []
            : await db.MemberInvites.AsNoTracking()
                .Where(i => inviteIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Label, ct);

        var joins = await RecordedJoinsAsync(db, tenantId, parsed, ct);

        var teamIds = parsed.Select(p => p.Row.TeamId).OfType<Guid>().Distinct().ToList();
        var teamNames = teamIds.Count == 0
            ? []
            : await db.Teams.AsNoTracking()
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        DateTime? accessEndedAt = null;
        if (memberSubjectId is { } narrowed
            && await db.TenantMembers.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.SubjectId == narrowed)
                .Select(m => m.AccessEndsAt)
                .FirstOrDefaultAsync(ct) is { } end
            && MembershipLiveness.HasEnded(end, now))
            accessEndedAt = DateTime.SpecifyKind(end, DateTimeKind.Utc);

        return new MemberHistoryPage
        {
            Data = parsed.Select(p => ToEntry(p.Row, p.Details, names, labels, joins, teamNames)).ToList(),
            Pagination = new PaginationInfo(limit, offset, total),
            AccessEndedAt = accessEndedAt,
        };
    }

    /// <summary>
    /// The (member, invite, commit time) of the joined rows that stand beside the page's invite changes:
    /// rows of one commit share <c>created_at</c>, so a joined row of another commit, such as a later
    /// rejoin through the same invite, does not count.
    /// </summary>
    private static async Task<HashSet<(Guid Member, Guid Invite, DateTime At)>> RecordedJoinsAsync(
        NocturneDbContext db, Guid tenantId, List<(AuthAuditLogEntity Row, Details Details)> parsed, CancellationToken ct)
    {
        var changes = parsed
            .Where(p => p.Details.InviteId is not null && p.Row.EventType != AuthAuditEventType.MemberJoined
                && p.Row.TargetSubjectId is not null)
            .Select(p => p.Row)
            .ToList();
        if (changes.Count == 0)
            return [];

        var members = changes.Select(r => r.TargetSubjectId!.Value).Distinct().ToList();
        var times = changes.Select(r => r.CreatedAt).Distinct().ToList();
        var joined = await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.EventType == AuthAuditEventType.MemberJoined
                && a.TargetSubjectId != null
                && members.Contains(a.TargetSubjectId.Value)
                && times.Contains(a.CreatedAt))
            .Select(a => new { Member = a.TargetSubjectId!.Value, a.DetailsJson, a.CreatedAt })
            .ToListAsync(ct);
        return joined
            .Select(j => (j.Member, Invite: Details.Of(j.DetailsJson).InviteId, j.CreatedAt))
            .Where(j => j.Invite is not null)
            .Select(j => (j.Member, j.Invite!.Value, j.CreatedAt))
            .ToHashSet();
    }

    private static MemberHistoryEntry ToEntry(
        AuthAuditLogEntity row, Details details, Dictionary<Guid, string> names, Dictionary<Guid, string?> labels,
        HashSet<(Guid Member, Guid Invite, DateTime At)> joins, Dictionary<Guid, string> teamNames)
    {
        var given = row.EventType == AuthAuditEventType.RoleAssigned;
        var kind = row.EventType switch
        {
            AuthAuditEventType.HistoryLimitChanged => MemberHistoryKind.HistoryLimitChanged,
            AuthAuditEventType.RolePermissionsChanged => MemberHistoryKind.RolePermissionsChanged,
            AuthAuditEventType.MemberJoined => MemberHistoryKind.Joined,
            AuthAuditEventType.MemberRemoved => MemberHistoryKind.Removed,
            AuthAuditEventType.TeamAccessGiven => MemberHistoryKind.TeamAccessGiven,
            AuthAuditEventType.TeamAccessStopped => MemberHistoryKind.TeamAccessStopped,
            AuthAuditEventType.TeamAccessChanged => MemberHistoryKind.TeamAccessChanged,
            AuthAuditEventType.TeamStaffApproved => MemberHistoryKind.TeamStaffApproved,
            AuthAuditEventType.TeamStaffDeclined => MemberHistoryKind.TeamStaffDeclined,
            AuthAuditEventType.TeamStaffRuleChanged => MemberHistoryKind.TeamStaffRuleChanged,
            AuthAuditEventType.AccessEndChanged => details switch
            {
                { AccessEndRecorded: false } => MemberHistoryKind.AccessEndChanged,
                { AccessEndsAt: null } => MemberHistoryKind.AccessEndCleared,
                _ => MemberHistoryKind.AccessEndSet,
            },
            _ when details.Role is not null => given ? MemberHistoryKind.RoleGiven : MemberHistoryKind.RoleTaken,
            _ when details.Permission is not null => given ? MemberHistoryKind.PermissionGiven : MemberHistoryKind.PermissionTaken,
            _ => given ? MemberHistoryKind.AccessGiven : MemberHistoryKind.AccessTaken,
        };

        return new MemberHistoryEntry(
            row.Id,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            kind,
            row.TargetSubjectId is { } target ? new MemberHistoryPerson(target, names.GetValueOrDefault(target)) : null,
            ActorOf(row, names),
            details.Role,
            details.Permission,
            details.HistoryLimit,
            details.Gained,
            details.Lost,
            details.AccessEndsAt,
            details.InviteId is not null,
            details.InviteId is { } invite ? labels.GetValueOrDefault(invite) : null,
            kind == MemberHistoryKind.Joined ? details.JoinedHow : null,
            kind == MemberHistoryKind.Removed ? details.RemovedHow : null,
            details.By is { } by ? new MemberHistoryPerson(by, names.GetValueOrDefault(by)) : null,
            kind != MemberHistoryKind.Joined
                && row.TargetSubjectId is { } member
                && details.InviteId is { } via
                && joins.Contains((member, via, row.CreatedAt)),
            row.TeamId is { } team ? new MemberHistoryTeam(team, teamNames.GetValueOrDefault(team) ?? details.Name) : null,
            details.Roles,
            kind == MemberHistoryKind.TeamStaffRuleChanged ? details.TeamManagesStaff : null);
    }

    /// <summary>
    /// The credential name stored on audit rows written for an identity provider's token. No
    /// <see cref="AuthType"/> member carries it: nothing authenticates that way, and the rows remain.
    /// </summary>
    private const string StoredProviderTokenCredential = "OidcToken";

    private static MemberHistoryActor ActorOf(AuthAuditLogEntity row, Dictionary<Guid, string> names)
    {
        if (row.ActorSubjectId is { } subject)
            return new MemberHistoryActor(MemberHistoryActorKind.Person, subject, names.GetValueOrDefault(subject));

        var kind = row.ActorCredential?.Split(':', 2)[0] switch
        {
            AuthAuditRows.System => MemberHistoryActorKind.System,
            AuthAuditRows.Anonymous or nameof(AuthType.None) => MemberHistoryActorKind.Anonymous,
            nameof(AuthType.ApiKey) or nameof(AuthType.DirectGrant) => MemberHistoryActorKind.ApiKey,
            nameof(AuthType.OAuthAccessToken) or StoredProviderTokenCredential
                or nameof(AuthType.LegacyJwt) or nameof(AuthType.SessionCookie) => MemberHistoryActorKind.AccessToken,
            nameof(AuthType.InstanceKey) => MemberHistoryActorKind.InstanceKey,
            nameof(AuthType.Guest) => MemberHistoryActorKind.GuestLink,
            nameof(AuthType.PlatformAccess) => MemberHistoryActorKind.PlatformAccess,
            _ => MemberHistoryActorKind.Unknown,
        };
        return new MemberHistoryActor(kind, null, null);
    }

    private sealed record Details(
        string? Role, string? Permission, MemberHistoryLimit? HistoryLimit, Guid? InviteId,
        IReadOnlyList<string>? Gained, IReadOnlyList<string>? Lost, bool AccessEndRecorded, DateTime? AccessEndsAt,
        MembershipJoinHow? JoinedHow, MembershipEndHow? RemovedHow, Guid? By, string? Name, IReadOnlyList<string>? Roles,
        bool? TeamManagesStaff)
    {
        private static readonly Details Unknown = new(null, null, null, null, null, null, false, null, null, null, null, null, null, null);

        public static Details Of(string? json)
        {
            if (json is null)
                return Unknown;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unknown;

            var instant = Instant(root, MemberAccessAudit.AccessEndsAtKey);
            var how = Text(root, MemberAccessAudit.HowKey);
            return new Details(
                Text(root, MemberAccessAudit.RoleKey),
                Text(root, MemberAccessAudit.PermissionKey),
                Text(root, MemberAccessAudit.HistoryLimitKey) switch
                {
                    MemberAccessAudit.TwentyFourHours => MemberHistoryLimit.TwentyFourHours,
                    MemberAccessAudit.FullHistory => MemberHistoryLimit.Full,
                    _ => null,
                },
                Guid.TryParse(Text(root, MemberAccessAudit.InviteKey), out var invite) ? invite : null,
                List(root, MemberAccessAudit.GainedKey),
                List(root, MemberAccessAudit.LostKey),
                root.TryGetProperty(MemberAccessAudit.AccessEndsAtKey, out var end)
                    && (end.ValueKind == JsonValueKind.Null || instant is not null),
                instant,
                MemberAccessAudit.JoinedHow.Where(h => h.Value == how).Select(h => (MembershipJoinHow?)h.Key).FirstOrDefault(),
                MemberAccessAudit.RemovedHow.Where(h => h.Value == how).Select(h => (MembershipEndHow?)h.Key).FirstOrDefault(),
                Guid.TryParse(Text(root, MemberAccessAudit.ByKey), out var by) ? by : null,
                Text(root, TeamAudit.NameKey),
                List(root, TeamAudit.RolesKey),
                root.TryGetProperty(TeamAudit.TeamManagesStaffKey, out var manages)
                    && manages.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? manages.GetBoolean()
                    : null);
        }

        private static string? Text(JsonElement root, string key) =>
            root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static DateTime? Instant(JsonElement root, string key) =>
            DateTime.TryParse(Text(root, key), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
                ? instant
                : null;

        private static List<string>? List(JsonElement root, string key) =>
            root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : null;
    }
}
