using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>KeySystemEventsOnOriginalId</c> over two tenants holding system events that share an
/// <c>original_id</c>, on a real PostgreSQL as the migrator, which row level security binds. A
/// second database runs it up and back down.
/// </summary>
public class KeySystemEventsOnOriginalIdFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261123025556_AddAlertEvaluatedReadings";

    private static readonly DateTime Older = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newest = new(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid OldestInA = Guid.Parse("c0000000-0000-7000-8000-000000000009");
    internal static readonly Guid NewestInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid MiddleInA = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid SoloInA = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid NullIdInA = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid OtherNullIdInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid EmptyIdInA = Guid.Parse("c0000000-0000-7000-8000-000000000006");
    internal static readonly Guid OtherEmptyIdInA = Guid.Parse("c0000000-0000-7000-8000-000000000007");
    internal static readonly Guid TiedLowerIdInB = Guid.Parse("c0000000-0000-7000-8000-000000000010");
    internal static readonly Guid TiedHigherIdInB = Guid.Parse("c0000000-0000-7000-8000-000000000011");
    internal static readonly Guid SameKeyInB = Guid.Parse("c0000000-0000-7000-8000-000000000012");

    internal string UpMigrator { get; private set; } = string.Empty;
    internal string DownMigrator { get; private set; } = string.Empty;
    internal Dictionary<Guid, string?> BeforeUp { get; } = [];
    internal Dictionary<Guid, string?> AfterUp { get; } = [];

    public async Task InitializeAsync()
    {
        var up = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "key_system_events");
        UpMigrator = up.MigratorConnectionString;
        await SeedAsync(UpMigrator);
        await ReadAsync(UpMigrator, BeforeUp);
        await MigrateAsync(UpMigrator);
        await ReadAsync(UpMigrator, AfterUp);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "key_system_events_down");
        DownMigrator = down.MigratorConnectionString;
        await SeedAsync(DownMigrator);
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static async Task<string?> InsertSqlStateAsync(string connectionString, Guid tenant, string? originalId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            {InsertStatement(Guid.CreateVersion7(), tenant, originalId, DateTime.UtcNow)}
            """, conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static string InsertStatement(Guid id, Guid tenant, string? originalId, DateTime createdAt)
    {
        var key = originalId is null ? "NULL" : $"'{originalId}'";
        return $"""
            INSERT INTO system_events (id, tenant_id, event_type, category, mills, created_at, original_id)
            VALUES ('{id}', '{tenant}', 'Alarm', 'Pump', 1000, '{createdAt:O}', {key});
            """;
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{TenantA}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{TenantA}', false);
            {InsertStatement(OldestInA, TenantA, "dup", Older)}
            {InsertStatement(NewestInA, TenantA, "dup", Newest)}
            {InsertStatement(MiddleInA, TenantA, "dup", Newer)}
            {InsertStatement(SoloInA, TenantA, "solo", Older)}
            {InsertStatement(NullIdInA, TenantA, null, Older)}
            {InsertStatement(OtherNullIdInA, TenantA, null, Older)}
            {InsertStatement(EmptyIdInA, TenantA, "", Older)}
            {InsertStatement(OtherEmptyIdInA, TenantA, "", Older)}

            SELECT set_config('app.current_tenant_id', '{TenantB}', false);
            {InsertStatement(TiedLowerIdInB, TenantB, "dup", Newer)}
            {InsertStatement(TiedHigherIdInB, TenantB, "dup", Newer)}
            {InsertStatement(SameKeyInB, TenantB, "solo", Older)}
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ReadAsync(string connectionString, Dictionary<Guid, string?> rows)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, original_id FROM system_events;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class KeySystemEventsOnOriginalIdTests(KeySystemEventsOnOriginalIdFixture fixture)
    : IClassFixture<KeySystemEventsOnOriginalIdFixture>
{
    [Fact]
    public void The_seed_holds_the_duplicates_the_migration_must_remove()
    {
        fixture.BeforeUp.Should().HaveCount(11);
        fixture.BeforeUp.Count(r => r.Value == "dup").Should().Be(5);
        fixture.BeforeUp.Count(r => r.Value == "").Should().Be(2);
    }

    [Fact]
    public void Each_tenant_keeps_the_row_written_last_for_a_key()
    {
        var live = fixture.AfterUp.Keys;

        live.Should().Contain(KeySystemEventsOnOriginalIdFixture.NewestInA)
            .And.NotContain([KeySystemEventsOnOriginalIdFixture.OldestInA, KeySystemEventsOnOriginalIdFixture.MiddleInA]);
    }

    [Fact]
    public void A_tie_on_creation_time_keeps_the_highest_id()
    {
        var live = fixture.AfterUp.Keys;

        live.Should().Contain(KeySystemEventsOnOriginalIdFixture.TiedHigherIdInB)
            .And.NotContain(KeySystemEventsOnOriginalIdFixture.TiedLowerIdInB);
    }

    [Fact]
    public void One_key_in_two_tenants_keeps_a_row_in_each()
    {
        fixture.AfterUp.Where(r => r.Value == "dup").Select(r => r.Key).Should().BeEquivalentTo(
            [KeySystemEventsOnOriginalIdFixture.NewestInA, KeySystemEventsOnOriginalIdFixture.TiedHigherIdInB]);
        fixture.AfterUp.Where(r => r.Value == "solo").Select(r => r.Key).Should().BeEquivalentTo(
            [KeySystemEventsOnOriginalIdFixture.SoloInA, KeySystemEventsOnOriginalIdFixture.SameKeyInB]);
    }

    [Fact]
    public void Unkeyed_and_empty_keyed_events_are_all_kept_and_an_empty_id_becomes_null()
    {
        fixture.AfterUp.Should().ContainKeys(
            KeySystemEventsOnOriginalIdFixture.NullIdInA,
            KeySystemEventsOnOriginalIdFixture.OtherNullIdInA,
            KeySystemEventsOnOriginalIdFixture.EmptyIdInA,
            KeySystemEventsOnOriginalIdFixture.OtherEmptyIdInA);
        fixture.AfterUp.Should().HaveCount(8);
        fixture.AfterUp[KeySystemEventsOnOriginalIdFixture.EmptyIdInA].Should().BeNull();
        fixture.AfterUp[KeySystemEventsOnOriginalIdFixture.OtherEmptyIdInA].Should().BeNull();
        fixture.AfterUp.Values.Should().NotContain("");
    }

    [Fact]
    public async Task The_key_is_a_valid_unique_index_over_tenant_and_id_that_replaces_the_plain_one()
    {
        var definition = (string?)await NpgsqlTestCommands.ScalarAsync<object?>(fixture.UpMigrator, """
            SELECT pg_get_indexdef(i.indexrelid)
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_system_events_tenant_original_id' AND i.indisunique AND i.indisvalid
            """);

        definition.Should().NotBeNull().And.Contain("(tenant_id, original_id)");
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.UpMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_system_events_original_id'")).Should().Be(0L);
    }

    [Fact]
    public async Task A_second_row_for_a_key_is_refused_but_an_unkeyed_one_and_another_tenants_are_not()
    {
        var a = KeySystemEventsOnOriginalIdFixture.TenantA;
        var b = KeySystemEventsOnOriginalIdFixture.TenantB;

        (await KeySystemEventsOnOriginalIdFixture.InsertSqlStateAsync(fixture.UpMigrator, a, "solo"))
            .Should().Be(PostgresErrorCodes.UniqueViolation);
        (await KeySystemEventsOnOriginalIdFixture.InsertSqlStateAsync(fixture.UpMigrator, a, null)).Should().BeNull();
        (await KeySystemEventsOnOriginalIdFixture.InsertSqlStateAsync(fixture.UpMigrator, b, "fresh")).Should().BeNull();
        (await KeySystemEventsOnOriginalIdFixture.InsertSqlStateAsync(fixture.UpMigrator, a, "fresh")).Should().BeNull();
    }

    [Fact]
    public async Task Down_restores_the_plain_index_and_drops_the_unique_one_and_keeps_the_rows()
    {
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_system_events_tenant_original_id'")).Should().Be(0L);
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator, """
            SELECT NOT i.indisunique AND i.indisvalid
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_system_events_original_id'
            """)).Should().Be(true);
        (await KeySystemEventsOnOriginalIdFixture.InsertSqlStateAsync(
            fixture.DownMigrator, KeySystemEventsOnOriginalIdFixture.TenantA, "solo")).Should().BeNull();
    }
}
