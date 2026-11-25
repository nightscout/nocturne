using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>BackfillTenantMemberReasons</c> on a real PostgreSQL as the migrator, over an owner, the
/// Public member, the device member and an invited member with every access column set: each gets
/// exactly one direct reason carrying its row's access, no row changes, and a second run inserts
/// nothing.
/// </summary>
public class BackfillTenantMemberReasonsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261122112333_AddTenantMemberReasons";
    private const string Backfill = "20261122120454_BackfillTenantMemberReasons";

    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111113842");
    internal static readonly Guid OwnerRole = Guid.Parse("b1111111-1111-7111-8111-111111113842");
    internal static readonly Guid ViewerRole = Guid.Parse("b2222222-2222-7222-8222-222222223842");
    internal static readonly Guid CaretakerRole = Guid.Parse("b3333333-3333-7333-8333-333333333842");
    internal static readonly Guid Invite = Guid.Parse("c1111111-1111-7111-8111-111111113842");
    internal static readonly Guid Owner = Guid.Parse("e1111111-1111-7111-8111-111111113842");
    internal static readonly Guid Public = Guid.Parse("e2222222-2222-7222-8222-222222223842");
    internal static readonly Guid Device = Guid.Parse("e3333333-3333-7333-8333-333333333842");
    internal static readonly Guid Invited = Guid.Parse("e4444444-4444-7444-8444-444444443842");

    private static readonly DateTime Stamp = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    private string _migrator = string.Empty;

    internal List<string> RowsBefore { get; private set; } = [];
    internal List<string> RowsAfter { get; private set; } = [];
    internal Dictionary<Guid, ReasonRow> Reasons { get; private set; } = [];
    internal int RowsInsertedBySecondRun { get; private set; }

    internal sealed record ReasonRow(
        int Count, Guid Id, string Kind, Guid? SourceId, string? DirectPermissions, bool LimitTo24Hours,
        DateTime? AccessEndsAt, Guid? CreatedFromInviteId, List<Guid> RoleIds);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "backfill_member_reasons");
        _migrator = database.MigratorConnectionString;

        await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{Tenant}', 'backfill-reasons', 'backfill-reasons', true, now(), now());
            INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
            VALUES ('{OwnerRole}', '{Tenant}', 'Owner', 'owner', '["*"]', true, now(), now()),
                   ('{ViewerRole}', '{Tenant}', 'Viewer', 'viewer', '["glucose.read"]', true, now(), now()),
                   ('{CaretakerRole}', '{Tenant}', 'Caretaker', 'caretaker', '["treatments.write"]', true, now(), now());
            """);
        foreach (var subject in new[] { Owner, Public, Device, Invited })
        {
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES ('{subject}', 'backfill-{subject:N}', true, {(subject == Public || subject == Device ? "true" : "false")}, now(), now(), 'Approved');
                """);
        }

        await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{Tenant}', false);
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES ('{Invite}', '{Tenant}', '{Owner}', '{Invite:N}', true, now() + interval '1 day', 1, now());
            """);
        await InsertMemberAsync(Owner, "NULL", false, "NULL", "NULL", OwnerRole);
        await InsertMemberAsync(Public, "NULL", false, "NULL", "NULL");
        await InsertMemberAsync(Device, """'["*"]'""", false, "NULL", "NULL");
        await InsertMemberAsync(Invited, """'["glucose.read","treatments.read"]'""", true, "'2026-12-31T00:00:00Z'", $"'{Invite}'",
            ViewerRole, CaretakerRole);

        RowsBefore = await ReadRowsAsync();

        await using (var context = new NocturneDbContext(Options()))
            await context.GetService<IMigrator>().MigrateAsync(Backfill);

        RowsAfter = await ReadRowsAsync();
        Reasons = await ReadReasonsAsync();
        RowsInsertedBySecondRun = await NpgsqlTestCommands.ExecuteAsync(_migrator, BackfillTenantMemberReasons.BackfillSql);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task InsertMemberAsync(
        Guid member, string permissions, bool limit, string accessEndsAt, string invite, params Guid[] roles)
    {
        await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, limit_to_24_hours, access_ends_at,
                                        created_from_invite_id, sys_created_at, sys_updated_at)
            VALUES ('{member}', '{Tenant}', '{member}', {permissions}, {(limit ? "true" : "false")}, {accessEndsAt},
                    {invite}, '{Stamp:O}', '{Stamp:O}');
            """);
        foreach (var role in roles)
        {
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id, sys_created_at)
                VALUES ('{Guid.CreateVersion7()}', '{member}', '{role}', now());
                """);
        }
    }

    private DbContextOptions<NocturneDbContext> Options() =>
        TestContexts.MigrationsOptions(_migrator);

    private async Task<List<string>> ReadRowsAsync()
    {
        var rows = new List<string>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT row_to_json(m)::text || '|' ||
                   coalesce((SELECT string_agg(row_to_json(mr)::text, ',' ORDER BY mr.id)
                             FROM tenant_member_roles mr WHERE mr.tenant_member_id = m.id), '')
            FROM tenant_members m ORDER BY m.id
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task<Dictionary<Guid, ReasonRow>> ReadReasonsAsync()
    {
        var reasons = new Dictionary<Guid, ReasonRow>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT r.tenant_member_id, count(*) OVER (PARTITION BY r.tenant_member_id), r.id, r.kind, r.source_id,
                   r.direct_permissions::text, r.limit_to_24_hours, r.access_ends_at, r.created_from_invite_id,
                   coalesce(array_agg(rr.tenant_role_id ORDER BY rr.tenant_role_id) FILTER (WHERE rr.id IS NOT NULL), '{}')
            FROM tenant_member_reasons r
            LEFT JOIN tenant_member_reason_roles rr ON rr.tenant_member_reason_id = r.id
            GROUP BY r.id
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reasons[reader.GetGuid(0)] = new ReasonRow(
                (int)reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetFieldValue<Guid[]>(9).ToList());
        }

        return reasons;
    }
}

[Trait("Category", "Integration")]
public class BackfillTenantMemberReasonsTests(BackfillTenantMemberReasonsFixture fixture)
    : IClassFixture<BackfillTenantMemberReasonsFixture>
{
    [Fact]
    public void Every_row_and_its_role_links_are_unchanged()
    {
        fixture.RowsAfter.Should().Equal(fixture.RowsBefore).And.HaveCount(4);
    }

    [Fact]
    public void Every_member_has_exactly_one_direct_reason_with_its_id()
    {
        fixture.Reasons.Keys.Should().BeEquivalentTo([
            BackfillTenantMemberReasonsFixture.Owner, BackfillTenantMemberReasonsFixture.Public,
            BackfillTenantMemberReasonsFixture.Device, BackfillTenantMemberReasonsFixture.Invited]);
        foreach (var (member, reason) in fixture.Reasons)
        {
            reason.Count.Should().Be(1);
            reason.Id.Should().Be(member);
            reason.Kind.Should().Be("direct");
            reason.SourceId.Should().BeNull();
        }
    }

    [Fact]
    public void The_owner_reason_carries_the_owner_role()
    {
        var reason = fixture.Reasons[BackfillTenantMemberReasonsFixture.Owner];
        reason.RoleIds.Should().Equal(BackfillTenantMemberReasonsFixture.OwnerRole);
        reason.DirectPermissions.Should().BeNull();
        reason.LimitTo24Hours.Should().BeFalse();
        reason.AccessEndsAt.Should().BeNull();
        reason.CreatedFromInviteId.Should().BeNull();
    }

    [Fact]
    public void The_public_reason_gives_nothing()
    {
        var reason = fixture.Reasons[BackfillTenantMemberReasonsFixture.Public];
        reason.RoleIds.Should().BeEmpty();
        reason.DirectPermissions.Should().BeNull();
    }

    [Fact]
    public void The_device_reason_gives_full_access_and_no_role()
    {
        var reason = fixture.Reasons[BackfillTenantMemberReasonsFixture.Device];
        reason.RoleIds.Should().BeEmpty();
        reason.DirectPermissions.Should().Be("""["*"]""");
    }

    [Fact]
    public void The_invited_reason_carries_every_access_column_and_the_invite()
    {
        var reason = fixture.Reasons[BackfillTenantMemberReasonsFixture.Invited];
        reason.RoleIds.Should().BeEquivalentTo([BackfillTenantMemberReasonsFixture.ViewerRole, BackfillTenantMemberReasonsFixture.CaretakerRole]);
        reason.DirectPermissions.Should().Be("""["glucose.read", "treatments.read"]""");
        reason.LimitTo24Hours.Should().BeTrue();
        reason.AccessEndsAt.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        reason.CreatedFromInviteId.Should().Be(BackfillTenantMemberReasonsFixture.Invite);
    }

    [Fact]
    public void A_second_run_inserts_nothing() => fixture.RowsInsertedBySecondRun.Should().Be(0);
}
