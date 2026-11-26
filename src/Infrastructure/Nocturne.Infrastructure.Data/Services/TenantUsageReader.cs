using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Services;

/// <inheritdoc cref="ITenantUsageReader"/>
/// <remarks>
/// <para>
/// A page costs four round trips whatever its size. The platform tables (tenants, memberships,
/// subjects, the chat directory) carry no tenant policy, so one query each reads them for the whole
/// page. Every other table forces row level security to the tenant in <c>app.current_tenant_id</c>, so
/// no single statement can read two tenants. One batch in one transaction sets that GUC local to the
/// transaction before each tenant's statements, and each statement also names its tenant. The
/// transaction is rolled back, which discards the GUC.
/// </para>
/// <para>
/// Every time leaves as its UTC date. A connector's error message is read to classify it
/// (<see cref="ConnectorErrorCategory"/>) and goes no further.
/// </para>
/// </remarks>
public sealed class TenantUsageReader(INocturneDbContextFactory contexts, TimeProvider time) : ITenantUsageReader
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(7);

    private const string SetTenantSql = "SELECT set_config('app.current_tenant_id', $1, true)";

    private const string CountsSql = """
        SELECT
            (SELECT count(*) FROM alert_rules WHERE tenant_id = $1)::int,
            (SELECT coalesce(array_agg(DISTINCT channel_type ORDER BY channel_type), '{}')
               FROM alert_rule_channels WHERE tenant_id = $1),
            (SELECT count(*) FROM report_specs WHERE tenant_id = $1 AND deleted_at IS NULL)::int,
            (SELECT count(*) FROM tracker_definitions WHERE tenant_id = $1)::int,
            (SELECT count(*) FROM clock_faces WHERE tenant_id = $1)::int,
            (SELECT count(*) FROM oauth_grants
               WHERE tenant_id = $1 AND grant_type = $2 AND revoked_at IS NULL
                 AND (expires_at IS NULL OR expires_at > $3))::int,
            (SELECT count(*) FROM oauth_clients WHERE tenant_id = $1)::int,
            (SELECT count(*) FROM coach_mark_states
               WHERE tenant_id = $1 AND (seen_at IS NOT NULL OR completed_at IS NOT NULL))::int
        """;

    private const string ConnectorsSql = """
        SELECT connector_name, configuration::text, last_successful_sync, last_error_at,
               last_error_message, is_healthy
        FROM connector_configurations
        WHERE tenant_id = $1
        ORDER BY connector_name
        """;

    private const string DevicesSql = """
        SELECT kind, max(last_seen_at)
        FROM client_devices
        WHERE tenant_id = $1
        GROUP BY kind
        ORDER BY kind
        """;

    public async Task<IReadOnlyList<TenantUsage>> ReadAsync(
        Guid? tenantId, int offset, int limit, Func<string, TimeSpan> staleAfter, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreatePlatformAsync(ct);

        var tenantQuery = db.Tenants.AsNoTracking().Where(t => !t.IsDemo);
        if (tenantId is { } only)
            tenantQuery = tenantQuery.Where(t => t.Id == only);

        var tenants = await tenantQuery
            .OrderBy(t => t.Id)
            .Skip(offset)
            .Take(limit)
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.IsActive,
                t.LastReadingAt,
                t.OnboardingCompletedAt,
                t.SetupHubEnrolledAt,
                ShareEnabled = t.ShareToken != null,
                t.ShareLastAccessedAt,
            })
            .ToListAsync(ct);
        if (tenants.Count == 0)
            return [];

        var ids = tenants.Select(t => t.Id).ToList();

        var members = (await db.TenantMembers.AsNoTracking()
                .Where(m => ids.Contains(m.TenantId))
                .LiveAt(now)
                .Where(m => !m.Subject!.IsSystemSubject && !m.Subject.IsDemoSubject && m.Subject.IsActive)
                .Select(m => new
                {
                    m.TenantId,
                    m.LastUsedAt,
                    IsOwner = m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner),
                    m.Subject!.LastLoginAt,
                })
                .ToListAsync(ct))
            .ToLookup(m => m.TenantId);

        var chatLinked = (await db.ChatIdentityDirectory.AsNoTracking()
                .Where(c => c.IsActive && ids.Contains(c.TenantId))
                .Select(c => c.TenantId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var scoped = await ReadTenantScopedAsync(db, ids, now, staleAfter, ct);

        var activeSince = now - ActiveWindow;
        return tenants.Select(t =>
        {
            var people = members[t.Id].ToList();
            var own = scoped[t.Id];
            return new TenantUsage
            {
                TenantId = t.Id,
                Slug = t.Slug,
                IsActive = t.IsActive,
                LastReadingOn = On(t.LastReadingAt),
                OnboardingCompletedOn = On(t.OnboardingCompletedAt),
                SetupHubEnrolledOn = On(t.SetupHubEnrolledAt),
                LastOwnerLoginOn = On(people.Where(p => p.IsOwner).Max(p => p.LastLoginAt)),
                LastMemberActivityOn = On(people.Max(p => p.LastUsedAt)),
                MemberCount = people.Count,
                MembersActiveLast7Days = people.Count(p => p.LastUsedAt >= activeSince),
                Connectors = own.Connectors,
                AlertRuleCount = own.AlertRuleCount,
                AlertChannelTypes = own.AlertChannelTypes,
                SavedReportCount = own.SavedReportCount,
                TrackerCount = own.TrackerCount,
                ClockFaceCount = own.ClockFaceCount,
                GuestLinkCount = own.GuestLinkCount,
                PublicShareEnabled = t.ShareEnabled,
                ShareLastAccessedOn = On(t.ShareLastAccessedAt),
                ClientDevices = own.ClientDevices,
                ChatIdentityLinked = chatLinked.Contains(t.Id),
                OauthClientCount = own.OauthClientCount,
                CoachMarksSeenCount = own.CoachMarksSeenCount,
            };
        }).ToList();
    }

    private static async Task<Dictionary<Guid, TenantScopedUsage>> ReadTenantScopedAsync(
        NocturneDbContext db,
        IReadOnlyList<Guid> tenantIds,
        DateTime now,
        Func<string, TimeSpan> staleAfter,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var batch = new NpgsqlBatch(
            (NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction());

        foreach (var tenantId in tenantIds)
        {
            batch.BatchCommands.Add(Command(SetTenantSql, tenantId.ToString()));
            batch.BatchCommands.Add(Command(CountsSql, tenantId, OAuthGrantTypes.Guest, now));
            batch.BatchCommands.Add(Command(ConnectorsSql, tenantId));
            batch.BatchCommands.Add(Command(DevicesSql, tenantId));
        }

        var usage = new Dictionary<Guid, TenantScopedUsage>(tenantIds.Count);
        await using (var reader = await batch.ExecuteReaderAsync(ct))
        {
            var first = true;
            foreach (var tenantId in tenantIds)
            {
                if (!first)
                    await reader.NextResultAsync(ct);
                first = false;

                await reader.NextResultAsync(ct);
                await reader.ReadAsync(ct);
                var counts = new
                {
                    AlertRules = reader.GetInt32(0),
                    ChannelTypes = reader.GetFieldValue<string[]>(1),
                    Reports = reader.GetInt32(2),
                    Trackers = reader.GetInt32(3),
                    ClockFaces = reader.GetInt32(4),
                    GuestLinks = reader.GetInt32(5),
                    OauthClients = reader.GetInt32(6),
                    CoachMarks = reader.GetInt32(7),
                };

                await reader.NextResultAsync(ct);
                var connectors = new List<ConnectorUsage>();
                while (await reader.ReadAsync(ct))
                {
                    var type = reader.GetString(0);
                    var lastSuccessfulSync = Utc(reader, 2);
                    var lastErrorAt = Utc(reader, 3);
                    var isHealthy = reader.GetBoolean(5);
                    connectors.Add(new ConnectorUsage(
                        type,
                        ConnectorEnabledKey.IsEnabled(reader.GetString(1)),
                        ConnectorSyncStatus.Of(isHealthy, lastErrorAt, lastSuccessfulSync, now, staleAfter(type)),
                        On(lastSuccessfulSync),
                        On(lastErrorAt),
                        ConnectorErrorCategory.Classify(isHealthy, reader.IsDBNull(4) ? null : reader.GetString(4))));
                }

                await reader.NextResultAsync(ct);
                var devices = new List<ClientDeviceUsage>();
                while (await reader.ReadAsync(ct))
                    devices.Add(new ClientDeviceUsage(reader.GetString(0), On(Utc(reader, 1))!.Value));

                usage[tenantId] = new TenantScopedUsage(
                    connectors, counts.AlertRules, counts.ChannelTypes, counts.Reports, counts.Trackers,
                    counts.ClockFaces, counts.GuestLinks, devices, counts.OauthClients, counts.CoachMarks);
            }
        }

        await transaction.RollbackAsync(ct);
        return usage;
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }

    private static DateOnly? On(DateTime? utc) => utc is { } at ? DateOnly.FromDateTime(at) : null;

    private static DateTime? Utc(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    private sealed record TenantScopedUsage(
        IReadOnlyList<ConnectorUsage> Connectors,
        int AlertRuleCount,
        IReadOnlyList<string> AlertChannelTypes,
        int SavedReportCount,
        int TrackerCount,
        int ClockFaceCount,
        int GuestLinkCount,
        IReadOnlyList<ClientDeviceUsage> ClientDevices,
        int OauthClientCount,
        int CoachMarksSeenCount);
}
