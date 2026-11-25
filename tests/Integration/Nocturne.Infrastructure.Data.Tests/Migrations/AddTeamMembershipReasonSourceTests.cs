using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs every migration from before membership reasons existed through
/// <c>AddTeamMembershipReasonSource</c> on a real PostgreSQL as the migrator, over a database holding
/// memberships as an upgrading instance does: the backfill gives each a direct reason with no source,
/// which the new check accepts. A second database runs the migration up and back down.
/// </summary>
public class AddTeamMembershipReasonSourceFixture : IAsyncLifetime
{
    private const string BeforeReasons = "20261122093511_AddAlertExcursionOwedWork";
    internal const string PriorMigration = "20261123091244_RelabelInstanceWideRoleAuditRows";

    internal Guid Tenant { get; } = Guid.CreateVersion7();

    internal Guid PlainMember { get; } = Guid.CreateVersion7();

    internal Guid EndingMember { get; } = Guid.CreateVersion7();

    internal string Migrator { get; private set; } = string.Empty;

    /// <summary>The seeded members' reasons that are direct with no source, read right after the upgrade.</summary>
    internal long DirectWithoutSourceAfterUpgrade { get; private set; }

    /// <summary>The reasons of any other shape, read right after the upgrade.</summary>
    internal long OtherReasonsAfterUpgrade { get; private set; }

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(BeforeReasons, "team_reason_source");
        Migrator = database.MigratorConnectionString;
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{Tenant}', 'team-reason-{Tenant:N}', 'Site', true, now(), now());
            """);
        await SeedMemberAsync(Migrator, Tenant, PlainMember, "NULL", "NULL");
        await SeedMemberAsync(Migrator, Tenant, EndingMember, "'[\"glucose.read\"]'", "'2026-12-01T00:00:00Z'");
        await MigrateAsync(Migrator);
        DirectWithoutSourceAfterUpgrade = (long)(await NpgsqlTestCommands.ScalarAsync<object?>(Migrator, $"""
            SELECT count(*) FROM tenant_member_reasons
             WHERE tenant_member_id IN ('{PlainMember}', '{EndingMember}') AND kind = 'direct' AND source_id IS NULL
            """))!;
        OtherReasonsAfterUpgrade = (long)(await NpgsqlTestCommands.ScalarAsync<object?>(
            Migrator, "SELECT count(*) FROM tenant_member_reasons WHERE kind <> 'direct' OR source_id IS NOT NULL"))!;

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "team_reason_source_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A team with access to <see cref="Tenant"/>; returns the access's id.</summary>
    internal async Task<Guid> NewTeamAccessAsync()
    {
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        await using var conn = await NpgsqlTestCommands.OpenAsync(Migrator);
        await conn.PinTenantAsync(Tenant);
        await conn.ExecuteAsync($"""
            INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{access}', '{team}', '{Tenant}', false);
            """);
        return access;
    }

    internal Task InsertReasonAsync(Guid member, string kind, Guid? source) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours)
        VALUES ('{Guid.CreateVersion7()}', '{member}', '{kind}', {(source is { } s ? $"'{s}'" : "NULL")}, false);
        """);

    private static async Task SeedMemberAsync(string connectionString, Guid tenant, Guid member, string permissions, string endsAt)
    {
        var subject = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(connectionString, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'member-{subject:N}', true, false, now(), now(), 'Approved');
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, limit_to_24_hours, access_ends_at, sys_created_at, sys_updated_at)
            VALUES ('{member}', '{tenant}', '{subject}', {permissions}, false, {endsAt}, now(), now());
            """);
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamMembershipReasonSourceTests(AddTeamMembershipReasonSourceFixture fixture)
    : IClassFixture<AddTeamMembershipReasonSourceFixture>
{
    private const string Check = "ck_tenant_member_reasons_team_source";
    private const string ForeignKey = "FK_tenant_member_reasons_team_tenant_access_source_id";

    [Fact]
    public async Task Every_reason_an_upgraded_instance_holds_is_direct_with_no_source_and_passes_the_check()
    {
        fixture.DirectWithoutSourceAfterUpgrade.Should().Be(2);
        fixture.OtherReasonsAfterUpgrade.Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.Migrator,
            $"SELECT convalidated FROM pg_constraint WHERE conname = '{Check}'")).Should().Be(true);
    }

    [Fact]
    public async Task A_team_reason_names_an_access_and_no_other_kind_names_one()
    {
        var access = await fixture.NewTeamAccessAsync();

        var teamWithoutSource = () => fixture.InsertReasonAsync(fixture.PlainMember, "team", null);
        var directWithSource = () => fixture.InsertReasonAsync(fixture.EndingMember, "direct", access);

        (await teamWithoutSource.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await directWithSource.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task A_team_reason_must_name_an_access_that_exists_and_that_access_cannot_be_deleted_while_named()
    {
        var access = await fixture.NewTeamAccessAsync();
        var nowhere = () => fixture.InsertReasonAsync(fixture.PlainMember, "team", Guid.CreateVersion7());
        (await nowhere.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        await fixture.InsertReasonAsync(fixture.PlainMember, "team", access);
        var delete = async () =>
        {
            await using var conn = await NpgsqlTestCommands.OpenAsync(fixture.Migrator);
            await conn.PinTenantAsync(fixture.Tenant);
            await conn.ExecuteAsync($"DELETE FROM team_tenant_access WHERE id = '{access}';");
        };

        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Down_drops_the_check_the_foreign_key_and_its_index()
    {
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            $"SELECT count(*) FROM pg_constraint WHERE conname IN ('{Check}', '{ForeignKey}')")).Should().Be(0L);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_tenant_member_reasons_source_id'")).Should().Be(0L);
    }
}
