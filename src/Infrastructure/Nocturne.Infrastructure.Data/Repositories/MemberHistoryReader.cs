using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <inheritdoc cref="IMemberHistoryReader"/>
/// <remarks>
/// <c>auth_audit_log</c> is not tenant-scoped, so the <c>tenant_id</c> predicate is the isolation, and
/// the context is pinned to that tenant so the invite lookup reads under its row level security.
/// <see cref="MemberAccessAudit"/> and <c>SubjectService</c> write the same event types; the writer's
/// rows carry <c>kind</c> = <c>tenant</c>, which is how its rows are told from the global subject-role
/// rows and from a global role's permission change. Deleting a subject clears the details of every
/// row that names it, so a row with no details is kept and reads as a change whose detail is unknown.
/// A <c>SubjectService</c> row cleared the same way is indistinguishable from one of these when it
/// carries a tenant id, and reads the same way. An <c>access_end_changed</c> row is told apart by its
/// <c>access_ends_at</c> key: a string is the new end, a null is a cleared end, and no key is a row
/// whose detail is unknown.
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
    ];

    private const string MemberChangeRows =
        $"SELECT * FROM auth_audit_log WHERE details IS NULL OR details ->> '{MemberAccessAudit.KindKey}' = '{MemberAccessAudit.TenantKind}'";

    public async Task<PaginatedResponse<MemberHistoryEntry>> GetAsync(
        Guid tenantId, Guid? memberSubjectId, int limit, int offset, CancellationToken ct = default)
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

        var subjectIds = page
            .SelectMany(a => new[] { a.TargetSubjectId, a.ActorSubjectId })
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

        return new PaginatedResponse<MemberHistoryEntry>
        {
            Data = parsed.Select(p => ToEntry(p.Row, p.Details, names, labels)).ToList(),
            Pagination = new PaginationInfo(limit, offset, total),
        };
    }

    private static MemberHistoryEntry ToEntry(
        AuthAuditLogEntity row, Details details, Dictionary<Guid, string> names, Dictionary<Guid, string?> labels)
    {
        var given = row.EventType == AuthAuditEventType.RoleAssigned;
        var kind = row.EventType switch
        {
            AuthAuditEventType.HistoryLimitChanged => MemberHistoryKind.HistoryLimitChanged,
            AuthAuditEventType.RolePermissionsChanged => MemberHistoryKind.RolePermissionsChanged,
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
            details.InviteId is { } invite ? labels.GetValueOrDefault(invite) : null);
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
            MemberAccessAudit.System => MemberHistoryActorKind.System,
            MemberAccessAudit.Anonymous or nameof(AuthType.None) => MemberHistoryActorKind.Anonymous,
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
        IReadOnlyList<string>? Gained, IReadOnlyList<string>? Lost, bool AccessEndRecorded, DateTime? AccessEndsAt)
    {
        private static readonly Details Unknown = new(null, null, null, null, null, null, false, null);

        public static Details Of(string? json)
        {
            if (json is null)
                return Unknown;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unknown;

            var instant = Instant(root, MemberAccessAudit.AccessEndsAtKey);
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
                instant);
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
