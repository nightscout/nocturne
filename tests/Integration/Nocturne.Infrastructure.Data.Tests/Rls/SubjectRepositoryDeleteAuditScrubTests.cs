using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What <see cref="SubjectRepository.DeleteAsync"/> leaves of the deleted subject in
/// <c>mutation_audit_log</c>, <c>read_access_log</c>, <c>membership_requests</c> and
/// <c>in_app_notifications</c>, run as <c>nocturne_app</c> under the live policies, in a tenant the
/// subject still belongs to and in one it has left.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SubjectRepositoryDeleteAuditScrubTests : IAsyncLifetime
{
    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private readonly Guid _memberTenant = Guid.NewGuid();
    private readonly Guid _formerTenant = Guid.NewGuid();
    private readonly Guid _leaver = Guid.CreateVersion7();
    private readonly Guid _bystander = Guid.CreateVersion7();
    private readonly Dictionary<(Guid Tenant, Guid Actor), Guid> _requests = [];
    private readonly Dictionary<(Guid Tenant, string Key, Guid Named), string> _channels = [];

    public SubjectRepositoryDeleteAuditScrubTests(RlsCompletenessFixture fx) => _fx = fx;

    private Guid[] Tenants => [_memberTenant, _formerTenant];

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        foreach (var tenant in Tenants)
        {
            await conn.ExecuteAsync("""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'audit-scrub', true, now(), now())
                """, ("id", tenant), ("slug", $"scrub-{tenant:N}"));
        }

        foreach (var subject in new[] { _leaver, _bystander })
        {
            await conn.ExecuteAsync("""
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved')
                """, ("id", subject), ("name", Name(subject)));
            await conn.ExecuteAsync("""
                INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenant, @subject, '[]'::jsonb, now(), now(), false)
                """, ("id", Guid.CreateVersion7()), ("tenant", _memberTenant), ("subject", subject));
        }

        foreach (var tenant in Tenants)
        {
            await conn.PinTenantAsync(tenant);
            foreach (var actor in new[] { _leaver, _bystander })
            {
                await conn.ExecuteAsync("""
                    INSERT INTO mutation_audit_log (id, tenant_id, entity_type, action, actor_subject_id, actor_name, ip_address, created_at)
                    VALUES (gen_random_uuid(), @tenant, 'SensorGlucose', 'update', @actor, @name, @ip, now())
                    """, ("tenant", tenant), ("actor", actor), ("name", Name(actor)), ("ip", Ip(actor)));
                await conn.ExecuteAsync("""
                    INSERT INTO read_access_log (id, tenant_id, endpoint, status_code, actor_subject_id, actor_name, ip_address, user_agent, created_at)
                    VALUES (gen_random_uuid(), @tenant, 'GET /api/v4/sensor-glucoses', 200, @actor, @name, @ip, @agent, now())
                    """, ("tenant", tenant), ("actor", actor), ("name", Name(actor)), ("ip", Ip(actor)), ("agent", Agent(actor)));
                var request = _requests[(tenant, actor)] = Guid.CreateVersion7();
                await conn.ExecuteAsync("""
                    INSERT INTO membership_requests (id, tenant_id, subject_id, message, status, created_at)
                    VALUES (@id, @tenant, @actor, @message, 'pending', now())
                    """, ("id", request), ("tenant", tenant), ("actor", actor), ("message", Message(actor)));

                await InsertNoticeAsync(conn, tenant, _bystander, "membership.requested",
                    $"Membership request from {Name(actor)}", Message(actor), request.ToString());
                await InsertNoticeAsync(conn, tenant, _bystander, "passkey.anonymous_login_request",
                    $"Access request from {Name(actor)}", null, actor.ToString());
            }

            await InsertNoticeAsync(conn, tenant, _bystander, "tracker.alert", "Tracker due", null,
                _requests[(tenant, _leaver)].ToString());
            await InsertNoticeAsync(conn, tenant, _bystander, "sharing.link_rotated", "share_link_rotated", null,
                tenant.ToString());
            await InsertNoticeAsync(conn, tenant, _leaver, "system.update_available", "update_available", null, "1.2.3");
            await InsertNoticeAsync(conn, tenant, _leaver, "membership.approved", "Your membership request has been approved",
                null, _requests[(tenant, _leaver)].ToString());

            foreach (var (key, named) in ChannelNotices)
            {
                var channel = _channels[(tenant, key, named)] = Guid.CreateVersion7().ToString();
                await InsertNoticeAsync(conn, tenant, _bystander, NotificationSubjectNaming.AlertChannelNotSending,
                    "alert_channel_no_recipient", "alert_channel_no_recipient_subtitle", channel,
                    metadata: $$"""{"channelId":"{{channel}}","destinationLabel":"{{Name(named)}}","{{key}}":"{{named}}"}""");
            }
        }
    }

    private (string Key, Guid Named)[] ChannelNotices =>
    [
        (NotificationSubjectNaming.MemberSubjectIdKey, _leaver),
        (NotificationSubjectNaming.AddedBySubjectIdKey, _leaver),
        (NotificationSubjectNaming.MemberSubjectIdKey, _bystander),
        (NotificationSubjectNaming.AddedBySubjectIdKey, _bystander),
    ];

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_ClearsTheSubjectsClientInEveryTenant(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            var mutation = (await MutationRowsAsync(tenant))[_leaver];
            mutation.Ip.Should().BeNull($"tenant {tenant} holds the deleted subject's mutation row");

            var read = (await ReadRowsAsync(tenant))[_leaver];
            read.Ip.Should().BeNull($"tenant {tenant} holds the deleted subject's read row");
            read.Agent.Should().BeNull($"tenant {tenant} holds the deleted subject's read row");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_KeepsTheSubjectsNameAndId(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            (await MutationRowsAsync(tenant))[_leaver].Name.Should().Be(Name(_leaver));
            (await ReadRowsAsync(tenant))[_leaver].Name.Should().Be(Name(_leaver));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_LeavesAnotherActorsRowsUntouched(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            (await MutationRowsAsync(tenant))[_bystander]
                .Should().Be((Name(_bystander), (string?)Ip(_bystander)));
            (await ReadRowsAsync(tenant))[_bystander]
                .Should().Be((Name(_bystander), (string?)Ip(_bystander), (string?)Agent(_bystander)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_RemovesTheSubjectsMembershipRequestsInEveryTenantAndNoOneElses(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
            (await RequestersAsync(tenant)).Should().Equal([_bystander], $"tenant {tenant} held a request from each");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_RemovesTheNoticesThatNameTheSubjectAndTheOnesItHeldInEveryTenantAndNoOthers(bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            var notices = await NoticesAsync(tenant);

            notices.Should().NotContain(
                n => n.Title.Contains(Name(_leaver)) || n.Subtitle == Message(_leaver) || n.UserId == _leaver.ToString(),
                $"tenant {tenant} holds a notice naming the deleted subject or held by it");
            notices.Select(n => (n.UserId, n.Type, n.SourceId)).Should().BeEquivalentTo(
            [
                (_bystander.ToString(), "membership.requested", _requests[(tenant, _bystander)].ToString()),
                (_bystander.ToString(), "passkey.anonymous_login_request", _bystander.ToString()),
                (_bystander.ToString(), "tracker.alert", _requests[(tenant, _leaver)].ToString()),
                (_bystander.ToString(), "sharing.link_rotated", tenant.ToString()),
                (_bystander.ToString(), NotificationSubjectNaming.AlertChannelNotSending,
                    _channels[(tenant, NotificationSubjectNaming.MemberSubjectIdKey, _bystander)]),
                (_bystander.ToString(), NotificationSubjectNaming.AlertChannelNotSending,
                    _channels[(tenant, NotificationSubjectNaming.AddedBySubjectIdKey, _bystander)]),
            ], $"tenant {tenant} kept the other subject's notices, and a type the catalogue does not declare");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_RemovesTheChannelNoticesNamingTheSubjectAsMemberOrAdderInEveryTenantAndKeepsAnothersChannel(
        bool deletedFromTheMemberTenant)
    {
        await DeleteLeaverAsync(deletedFromTheMemberTenant);

        foreach (var tenant in Tenants)
        {
            var channels = (await NoticesAsync(tenant))
                .Where(n => n.Type == NotificationSubjectNaming.AlertChannelNotSending)
                .Select(n => n.SourceId);

            channels.Should().BeEquivalentTo(
                [
                    _channels[(tenant, NotificationSubjectNaming.MemberSubjectIdKey, _bystander)],
                    _channels[(tenant, NotificationSubjectNaming.AddedBySubjectIdKey, _bystander)],
                ],
                $"tenant {tenant} held a notice for a channel the deleted subject receives, one for a channel it added, and the same for another person");
        }
    }

    [Fact]
    public async Task Delete_ReportsEachNoticeItRemovedFromAnotherPersonWithItsTenantAndHolder()
    {
        var expected = new List<(Guid Tenant, string UserId, string Type, string? SourceId)>();
        foreach (var tenant in Tenants)
        {
            expected.Add((tenant, _bystander.ToString(), "membership.requested", _requests[(tenant, _leaver)].ToString()));
            expected.Add((tenant, _bystander.ToString(), "passkey.anonymous_login_request", _leaver.ToString()));
            expected.Add((tenant, _bystander.ToString(), NotificationSubjectNaming.AlertChannelNotSending,
                _channels[(tenant, NotificationSubjectNaming.MemberSubjectIdKey, _leaver)]));
            expected.Add((tenant, _bystander.ToString(), NotificationSubjectNaming.AlertChannelNotSending,
                _channels[(tenant, NotificationSubjectNaming.AddedBySubjectIdKey, _leaver)]));
        }

        var change = await DeleteLeaverAsync(fromTheMemberTenant: false);

        change.RemovedNotices.Select(n => (n.TenantId, n.UserId, n.Notice.Type, n.Notice.SourceId))
            .Should().BeEquivalentTo(expected, "the notices the deleted subject held reach no open app");
    }

    [Fact]
    public async Task Delete_ByAReaderClampedToADay_RemovesTheOldAndTheArchivedNoticesToo()
    {
        await using (var conn = await _fx.OpenMigratorConnectionAsync())
        {
            foreach (var tenant in Tenants)
            {
                await conn.PinTenantAsync(tenant);
                var request = _requests[(tenant, _leaver)].ToString();
                await InsertNoticeAsync(conn, tenant, _bystander, "membership.requested",
                    $"Membership request from {Name(_leaver)}", Message(_leaver), request, createdDaysAgo: 10);
                await InsertNoticeAsync(conn, tenant, _bystander, "membership.requested",
                    $"Membership request from {Name(_leaver)}", Message(_leaver), request, createdDaysAgo: 3, archivedDaysAgo: 2);
            }
        }

        await DeleteLeaverAsync(fromTheMemberTenant: true, clamped: true);

        foreach (var tenant in Tenants)
        {
            (await NoticesAsync(tenant)).Should().NotContain(
                n => n.Title.Contains(Name(_leaver)) || n.Subtitle == Message(_leaver) || n.UserId == _leaver.ToString(),
                $"tenant {tenant} holds a notice the clamp hid from the deletion");
        }
    }

    private async Task<List<(string UserId, string Type, string? SourceId, string Title, string? Subtitle)>> NoticesAsync(Guid tenant)
    {
        var notices = new List<(string, string, string?, string, string?)>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, type, source_id, title, subtitle FROM in_app_notifications WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            notices.Add((reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetString(3), Text(reader, 4)));
        return notices;
    }

    private static Task InsertNoticeAsync(
        NpgsqlConnection conn, Guid tenant, Guid holder, string type, string title, string? subtitle, string sourceId,
        int createdDaysAgo = 0, int? archivedDaysAgo = null, string? metadata = null) =>
        conn.ExecuteAsync("""
            INSERT INTO in_app_notifications (id, tenant_id, user_id, type, category, urgency, title, subtitle, source_id, created_at, is_archived, archived_at, metadata_json)
            VALUES (gen_random_uuid(), @tenant, @holder, @type, 'ActionRequired', 'Info', @title, @subtitle, @source,
                now() - make_interval(days => @created), @archived::int IS NOT NULL, now() - make_interval(days => @archived::int),
                @metadata::jsonb)
            """,
            ("tenant", tenant), ("holder", holder.ToString()), ("type", type), ("title", title),
            ("subtitle", (object?)subtitle ?? DBNull.Value), ("source", sourceId), ("created", createdDaysAgo),
            ("archived", (object?)archivedDaysAgo ?? DBNull.Value), ("metadata", (object?)metadata ?? DBNull.Value));

    private async Task<List<Guid>> RequestersAsync(Guid tenant)
    {
        var requesters = new List<Guid>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT subject_id FROM membership_requests WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            requesters.Add(reader.GetGuid(0));
        return requesters;
    }

    /// <summary>
    /// Deletes as a reader with no tenant, as a platform admin on the apex does, or as a reader of the
    /// tenant the subject still belongs to, whose context the tenant query filter would hold there.
    /// </summary>
    private async Task<SubjectChange.Changed> DeleteLeaverAsync(bool fromTheMemberTenant, bool clamped = false)
    {
        await using var scope = _provider.CreateAsyncScope();
        if (fromTheMemberTenant)
            TenantReaderData.Resolve(scope.ServiceProvider, _memberTenant);
        if (clamped)
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();

        var repository = new SubjectRepository(scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>(), AccountLinkRoutes.Off);
        return (await repository.DeleteAsync(_leaver, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>().Subject;
    }

    private async Task<Dictionary<Guid, (string? Name, string? Ip)>> MutationRowsAsync(Guid tenant)
    {
        var rows = new Dictionary<Guid, (string?, string?)>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_subject_id, actor_name, ip_address FROM mutation_audit_log WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetGuid(0), (Text(reader, 1), Text(reader, 2)));
        return rows;
    }

    private async Task<Dictionary<Guid, (string? Name, string? Ip, string? Agent)>> ReadRowsAsync(Guid tenant)
    {
        var rows = new Dictionary<Guid, (string?, string?, string?)>();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await conn.PinTenantAsync(tenant);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT actor_subject_id, actor_name, ip_address, user_agent FROM read_access_log WHERE tenant_id = @tenant";
        cmd.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetGuid(0), (Text(reader, 1), Text(reader, 2), Text(reader, 3)));
        return rows;
    }

    private static string Name(Guid subject) => $"scrub-{subject:N}";

    private string Ip(Guid subject) => subject == _leaver ? "10.0.0.1" : "10.0.0.2";

    private static string Message(Guid subject) => $"message-{subject:N}";

    private static string Agent(Guid subject) => $"agent-{subject:N}";

    private static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
