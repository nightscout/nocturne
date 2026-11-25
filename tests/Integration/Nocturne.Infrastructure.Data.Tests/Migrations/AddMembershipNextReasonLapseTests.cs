using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddMembershipNextReasonLapse</c> on a real PostgreSQL as the migrator over a database
/// holding a membership. A second database runs it up and back down.
/// </summary>
public class AddMembershipNextReasonLapseFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261122212642_AddConnectorSetupToCaretaker";

    internal string Migrator { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingMember { get; } = Guid.CreateVersion7();

    internal Guid DownMember { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "next_reason_lapse");
        Migrator = database.MigratorConnectionString;
        await SeedMemberAsync(Migrator, ExistingMember);
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "next_reason_lapse_down");
        DownMigrator = down.MigratorConnectionString;
        await SeedMemberAsync(DownMigrator, DownMember);
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedMemberAsync(string connectionString, Guid member)
    {
        var tenant = Guid.CreateVersion7();
        var subject = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'lapse-{tenant:N}', 'Site', true, now(), now());
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'member', true, false, now(), now(), 'Approved');
            INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, access_ends_at, sys_created_at, sys_updated_at)
            VALUES ('{member}', '{tenant}', '{subject}', false, '2026-12-01T00:00:00Z', now(), now());
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddMembershipNextReasonLapseTests(AddMembershipNextReasonLapseFixture fixture)
    : IClassFixture<AddMembershipNextReasonLapseFixture>
{
    [Fact]
    public async Task An_existing_membership_has_no_next_lapse_and_keeps_its_end()
    {
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.Migrator,
            $"SELECT next_reason_lapse_at IS NULL FROM tenant_members WHERE id = '{fixture.ExistingMember}'")).Should().Be(true);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.Migrator,
            $"SELECT access_ends_at = '2026-12-01T00:00:00Z' FROM tenant_members WHERE id = '{fixture.ExistingMember}'")).Should().Be(true);
    }

    [Fact]
    public async Task The_next_lapse_is_indexed_only_where_it_is_set()
    {
        var definition = (string?)await NpgsqlTestCommands.ScalarAsync<object?>(fixture.Migrator,
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_tenant_members_next_reason_lapse_at'");
        definition.Should().NotBeNull();
        definition.Should().NotContain("UNIQUE").And.Contain("WHERE (next_reason_lapse_at IS NOT NULL)");
    }

    [Fact]
    public async Task Down_drops_the_column_and_its_index_and_keeps_the_membership()
    {
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_name = 'tenant_members' AND column_name = 'next_reason_lapse_at'
            """)).Should().Be(0L);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_tenant_members_next_reason_lapse_at'")).Should().Be(0L);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            $"SELECT count(*) FROM tenant_members WHERE id = '{fixture.DownMember}'")).Should().Be(1L);
    }
}
