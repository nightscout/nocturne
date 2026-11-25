using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTenantMemberReasons</c> on a real PostgreSQL as the migrator, over a database holding a
/// membership, and then exercises the keys and indexes of the two tables it creates.
/// </summary>
public class AddTenantMemberReasonsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261122093511_AddAlertExcursionOwedWork";

    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111113841");
    internal static readonly Guid Owner = Guid.Parse("c1111111-1111-7111-8111-111111113841");
    internal static readonly Guid SeededMember = Guid.Parse("e1111111-1111-7111-8111-111111113841");

    internal string Migrator { get; private set; } = string.Empty;

    internal long ReasonTablesBefore { get; private set; }
    internal long ReasonTablesAfter { get; private set; }
    internal long MembersBefore { get; private set; }
    internal long MembersAfter { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "tenant_member_reasons");
        Migrator = database.MigratorConnectionString;

        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{Tenant}', 'member-reasons', 'member-reasons', true, now(), now());
            """);
        await InsertSubjectAsync(Owner);
        await InsertMemberAsync(SeededMember, Owner);

        ReasonTablesBefore = await CountReasonTablesAsync();
        MembersBefore = await NpgsqlTestCommands.ScalarAsync<long>(Migrator, "SELECT count(*) FROM tenant_members");

        await using (var context = new NocturneDbContext(Options()))
            await context.GetService<IMigrator>().MigrateAsync();

        ReasonTablesAfter = await CountReasonTablesAsync();
        MembersAfter = await NpgsqlTestCommands.ScalarAsync<long>(Migrator, "SELECT count(*) FROM tenant_members");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<Guid> NewMemberAsync()
    {
        var subject = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await InsertSubjectAsync(subject);
        await InsertMemberAsync(member, subject);
        return member;
    }

    internal async Task<Guid> NewRoleAsync()
    {
        var role = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenant_roles (id, tenant_id, name, slug, permissions, is_system, sys_created_at, sys_updated_at)
            VALUES ('{role}', '{Tenant}', 'Role {role:N}', 'role-{role:N}', '[]', false, now(), now());
            """);
        return role;
    }

    internal async Task<Guid> NewInviteAsync()
    {
        var invite = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            SELECT set_config('app.current_tenant_id', '{Tenant}', false);
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES ('{invite}', '{Tenant}', '{Owner}', '{invite:N}', false, now() + interval '1 day', 0, now());
            """);
        return invite;
    }

    internal async Task<Guid> NewReasonAsync(Guid member, string kind, Guid? invite = null, Guid? source = null)
    {
        var reason = Guid.CreateVersion7();
        Guid? sourceId = kind == MembershipReasonKind.Direct ? null : source ?? Guid.CreateVersion7();
        if (sourceId is { } access)
            await NewTeamAccessAsync(access);
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, created_from_invite_id)
            VALUES ('{reason}', '{member}', '{kind}', {(sourceId is { } s ? $"'{s}'" : "NULL")},
                    false, {(invite is { } id ? $"'{id}'" : "NULL")});
            """);
        return reason;
    }

    /// <summary>A team's access to <see cref="Tenant"/> with id <paramref name="access"/>, which a team reason's source must name.</summary>
    private Task<int> NewTeamAccessAsync(Guid access)
    {
        var team = Guid.CreateVersion7();
        return NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            SELECT set_config('app.current_tenant_id', '{Tenant}', false);
            INSERT INTO teams (id, name) VALUES ('{team}', 'Team {team:N}');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{access}', '{team}', '{Tenant}', false)
            ON CONFLICT (id) DO NOTHING;
            """);
    }

    internal Task NewReasonRoleAsync(Guid reason, Guid role) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
        VALUES ('{Guid.CreateVersion7()}', '{reason}', '{role}');
        """);

    private Task<long> CountReasonTablesAsync() => NpgsqlTestCommands.ScalarAsync<long>(Migrator, """
        SELECT count(*) FROM information_schema.tables
         WHERE table_schema = 'public' AND table_name IN ('tenant_member_reasons', 'tenant_member_reason_roles')
        """);

    private Task<int> InsertSubjectAsync(Guid subject) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
        VALUES ('{subject}', 'member-reasons-{subject:N}', true, false, now(), now(), 'Approved');
        """);

    private Task<int> InsertMemberAsync(Guid member, Guid subject) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, sys_created_at, sys_updated_at)
        VALUES ('{member}', '{Tenant}', '{subject}', false, now(), now());
        """);

    private DbContextOptions<NocturneDbContext> Options() =>
        TestContexts.MigrationsOptions(Migrator);
}

[Trait("Category", "Integration")]
public class AddTenantMemberReasonsTests(AddTenantMemberReasonsFixture fixture)
    : IClassFixture<AddTenantMemberReasonsFixture>
{
    [Fact]
    public void The_migration_creates_both_tables_over_a_database_holding_memberships()
    {
        fixture.ReasonTablesBefore.Should().Be(0);
        fixture.ReasonTablesAfter.Should().Be(2);
        fixture.MembersAfter.Should().Be(fixture.MembersBefore).And.Be(1);
    }

    [Fact]
    public async Task Deleting_a_member_deletes_its_reasons_and_their_roles()
    {
        var member = await fixture.NewMemberAsync();
        var role = await fixture.NewRoleAsync();
        var direct = await fixture.NewReasonAsync(member, MembershipReasonKind.Direct);
        var other = await fixture.NewReasonAsync(member, "team");
        await fixture.NewReasonRoleAsync(direct, role);
        await fixture.NewReasonRoleAsync(other, role);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenant_members WHERE id = '{member}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{member}'"))
            .Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reason_roles WHERE tenant_member_reason_id IN ('{direct}', '{other}')"))
            .Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_roles WHERE id = '{role}'")).Should().Be(1);
    }

    [Fact]
    public async Task A_second_direct_reason_for_one_member_is_refused()
    {
        var member = await fixture.NewMemberAsync();
        await fixture.NewReasonAsync(member, MembershipReasonKind.Direct);

        var second = () => fixture.NewReasonAsync(member, MembershipReasonKind.Direct);

        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_reason_of_another_kind_may_sit_beside_the_direct_one()
    {
        var member = await fixture.NewMemberAsync();
        await fixture.NewReasonAsync(member, MembershipReasonKind.Direct);
        await fixture.NewReasonAsync(member, "team");
        await fixture.NewReasonAsync(member, "team");

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{member}'"))
            .Should().Be(3);
    }

    [Fact]
    public async Task A_second_reason_of_one_kind_from_one_source_is_refused_and_another_source_is_accepted()
    {
        var member = await fixture.NewMemberAsync();
        var source = Guid.CreateVersion7();
        await fixture.NewReasonAsync(member, "team", source: source);

        var again = () => fixture.NewReasonAsync(member, "team", source: source);

        (await again.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        await fixture.NewReasonAsync(member, "team", source: Guid.CreateVersion7());
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE tenant_member_id = '{member}'"))
            .Should().Be(2);
    }

    [Fact]
    public async Task One_role_is_given_once_by_a_reason()
    {
        var reason = await fixture.NewReasonAsync(await fixture.NewMemberAsync(), MembershipReasonKind.Direct);
        var role = await fixture.NewRoleAsync();
        await fixture.NewReasonRoleAsync(reason, role);

        var second = () => fixture.NewReasonRoleAsync(reason, role);

        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Deleting_a_role_removes_the_reason_roles_that_name_it_and_keeps_the_reason()
    {
        var member = await fixture.NewMemberAsync();
        var reason = await fixture.NewReasonAsync(member, MembershipReasonKind.Direct);
        var deleted = await fixture.NewRoleAsync();
        var kept = await fixture.NewRoleAsync();
        await fixture.NewReasonRoleAsync(reason, deleted);
        await fixture.NewReasonRoleAsync(reason, kept);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"DELETE FROM tenant_roles WHERE id = '{deleted}'");

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reason_roles WHERE tenant_member_reason_id = '{reason}' AND tenant_role_id = '{deleted}'"))
            .Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reason_roles WHERE tenant_member_reason_id = '{reason}' AND tenant_role_id = '{kept}'"))
            .Should().Be(1);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE id = '{reason}'")).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_an_invite_clears_the_reasons_invite_and_keeps_the_reason()
    {
        var invite = await fixture.NewInviteAsync();
        var reason = await fixture.NewReasonAsync(await fixture.NewMemberAsync(), MembershipReasonKind.Direct, invite);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE id = '{reason}' AND created_from_invite_id = '{invite}'"))
            .Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator, $"""
            SELECT set_config('app.current_tenant_id', '{AddTenantMemberReasonsFixture.Tenant}', false);
            DELETE FROM member_invites WHERE id = '{invite}';
            """);

        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, $"SELECT count(*) FROM tenant_member_reasons WHERE id = '{reason}' AND created_from_invite_id IS NULL"))
            .Should().Be(1);
    }
}
