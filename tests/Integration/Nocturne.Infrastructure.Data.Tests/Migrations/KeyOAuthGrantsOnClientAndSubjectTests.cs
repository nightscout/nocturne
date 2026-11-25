using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>KeyOAuthGrantsOnClientAndSubject</c> over two tenants holding several unrevoked app grants
/// of one subject for one client, on a real PostgreSQL as the migrator, which row level security
/// binds. A second database runs it up and back down.
/// </summary>
public class KeyOAuthGrantsOnClientAndSubjectFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261123082547_KeySystemEventsOnOriginalId";

    private static readonly DateTime Older = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newest = new(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime RevokedEarlier = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");
    internal static readonly Guid Subject = Guid.Parse("5b000000-0000-7000-8000-000000000001");
    internal static readonly Guid OtherSubject = Guid.Parse("5b000000-0000-7000-8000-000000000002");
    internal static readonly Guid ClientInA = Guid.Parse("c1000000-0000-7000-8000-000000000001");
    internal static readonly Guid OtherClientInA = Guid.Parse("c1000000-0000-7000-8000-000000000002");
    internal static readonly Guid ClientInB = Guid.Parse("c1000000-0000-7000-8000-000000000003");

    internal static readonly Guid OldestInA = Guid.Parse("90000000-0000-7000-8000-000000000009");
    internal static readonly Guid NewestInA = Guid.Parse("90000000-0000-7000-8000-000000000001");
    internal static readonly Guid MiddleInA = Guid.Parse("90000000-0000-7000-8000-000000000005");
    internal static readonly Guid RevokedBeforeInA = Guid.Parse("90000000-0000-7000-8000-000000000002");
    internal static readonly Guid OtherClientGrantInA = Guid.Parse("90000000-0000-7000-8000-000000000003");
    internal static readonly Guid OtherSubjectGrantInA = Guid.Parse("90000000-0000-7000-8000-000000000004");
    internal static readonly Guid DirectInA = Guid.Parse("90000000-0000-7000-8000-000000000006");
    internal static readonly Guid OtherDirectInA = Guid.Parse("90000000-0000-7000-8000-000000000007");
    internal static readonly Guid GuestInA = Guid.Parse("90000000-0000-7000-8000-000000000008");
    internal static readonly Guid TiedLowerIdInB = Guid.Parse("90000000-0000-7000-8000-000000000010");
    internal static readonly Guid TiedHigherIdInB = Guid.Parse("90000000-0000-7000-8000-000000000011");

    internal static readonly Guid TokenOfLoser = Guid.Parse("70000000-0000-7000-8000-000000000001");
    internal static readonly Guid TokenOfWinner = Guid.Parse("70000000-0000-7000-8000-000000000002");
    internal static readonly Guid TokenOfMiddle = Guid.Parse("70000000-0000-7000-8000-000000000003");
    internal static readonly Guid RevokedTokenOfLoser = Guid.Parse("70000000-0000-7000-8000-000000000004");
    internal static readonly Guid DeviceOfLoser = Guid.Parse("de000000-0000-7000-8000-000000000001");
    internal static readonly Guid DeviceOfWinner = Guid.Parse("de000000-0000-7000-8000-000000000002");
    internal static readonly Guid DeviceOfMiddle = Guid.Parse("de000000-0000-7000-8000-000000000003");
    internal static readonly DateTime TokenRevokedEarlier = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    internal string UpMigrator { get; private set; } = string.Empty;
    internal string DownMigrator { get; private set; } = string.Empty;
    internal Dictionary<Guid, DateTime?> RevokedBeforeUp { get; } = [];
    internal Dictionary<Guid, DateTime?> RevokedAfterUp { get; } = [];
    internal Dictionary<Guid, DateTime?> GrantsAfterDown { get; } = [];
    internal Dictionary<Guid, (Guid Grant, DateTime? RevokedAt)> TokensAfterUp { get; } = [];
    internal Dictionary<Guid, Guid> DevicesAfterUp { get; } = [];

    public async Task InitializeAsync()
    {
        var up = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "key_oauth_grants");
        UpMigrator = up.MigratorConnectionString;
        await SeedAsync(UpMigrator);
        await ReadGrantsAsync(UpMigrator, RevokedBeforeUp);
        await MigrateAsync(UpMigrator);
        await ReadGrantsAsync(UpMigrator, RevokedAfterUp);
        await ReadDependentsAsync(UpMigrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "key_oauth_grants_down");
        DownMigrator = down.MigratorConnectionString;
        await SeedAsync(DownMigrator);
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
        await ReadGrantsAsync(DownMigrator, GrantsAfterDown);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static async Task<string?> InsertSqlStateAsync(
        string connectionString, Guid tenant, Guid client, string grantType, DateTime? revokedAt)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            {GrantStatement(Guid.CreateVersion7(), tenant, Subject, client, grantType, DateTime.UtcNow, revokedAt)}
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

    private static string GrantStatement(
        Guid id, Guid tenant, Guid subject, Guid? client, string grantType, DateTime createdAt, DateTime? revokedAt)
    {
        var clientValue = client is { } c ? $"'{c}'" : "NULL";
        var revokedValue = revokedAt is { } r ? $"'{r:O}'" : "NULL";
        return $"""
            INSERT INTO oauth_grants (id, tenant_id, client_id, subject_id, grant_type, scopes, created_at, revoked_at)
            VALUES ('{id}', '{tenant}', {clientValue}, '{subject}', '{grantType}', ARRAY['glucose.read'], '{createdAt:O}', {revokedValue});
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
            INSERT INTO subjects (id, name) VALUES ('{Subject}', 'subject'), ('{OtherSubject}', 'other subject');

            SELECT set_config('app.current_tenant_id', '{TenantA}', false);
            INSERT INTO oauth_clients (id, tenant_id, client_id) VALUES
              ('{ClientInA}', '{TenantA}', 'client'),
              ('{OtherClientInA}', '{TenantA}', 'other-client');
            {GrantStatement(OldestInA, TenantA, Subject, ClientInA, "app", Older, null)}
            {GrantStatement(NewestInA, TenantA, Subject, ClientInA, "app", Newest, null)}
            {GrantStatement(MiddleInA, TenantA, Subject, ClientInA, "app", Newer, null)}
            {GrantStatement(RevokedBeforeInA, TenantA, Subject, ClientInA, "app", Older, RevokedEarlier)}
            {GrantStatement(OtherClientGrantInA, TenantA, Subject, OtherClientInA, "app", Older, null)}
            {GrantStatement(OtherSubjectGrantInA, TenantA, OtherSubject, ClientInA, "app", Older, null)}
            {GrantStatement(DirectInA, TenantA, Subject, null, "direct", Older, null)}
            {GrantStatement(OtherDirectInA, TenantA, Subject, null, "direct", Older, null)}
            {GrantStatement(GuestInA, TenantA, Subject, ClientInA, "guest", Older, null)}
            INSERT INTO oauth_refresh_tokens (id, tenant_id, grant_id, token_hash, expires_at, revoked_at) VALUES
              ('{TokenOfLoser}', '{TenantA}', '{OldestInA}', 'loser', '2030-01-01T00:00:00Z', NULL),
              ('{TokenOfWinner}', '{TenantA}', '{NewestInA}', 'winner', '2030-01-01T00:00:00Z', NULL),
              ('{TokenOfMiddle}', '{TenantA}', '{MiddleInA}', 'middle', '2030-01-01T00:00:00Z', NULL),
              ('{RevokedTokenOfLoser}', '{TenantA}', '{OldestInA}', 'revoked', '2030-01-01T00:00:00Z', '{TokenRevokedEarlier:O}');
            INSERT INTO client_devices (id, tenant_id, subject_id, install_id, kind, capabilities, grant_id) VALUES
              ('{DeviceOfLoser}', '{TenantA}', '{Subject}', 'loser', 'phone', ARRAY[]::text[], '{OldestInA}'),
              ('{DeviceOfWinner}', '{TenantA}', '{Subject}', 'winner', 'phone', ARRAY[]::text[], '{NewestInA}'),
              ('{DeviceOfMiddle}', '{TenantA}', '{Subject}', 'middle', 'phone', ARRAY[]::text[], '{MiddleInA}');

            SELECT set_config('app.current_tenant_id', '{TenantB}', false);
            INSERT INTO oauth_clients (id, tenant_id, client_id) VALUES ('{ClientInB}', '{TenantB}', 'client');
            {GrantStatement(TiedLowerIdInB, TenantB, Subject, ClientInB, "app", Newer, null)}
            {GrantStatement(TiedHigherIdInB, TenantB, Subject, ClientInB, "app", Newer, null)}
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ReadGrantsAsync(string connectionString, Dictionary<Guid, DateTime?> grants)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await PinAsync(conn, tenant);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, revoked_at FROM oauth_grants;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                grants[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
    }

    private async Task ReadDependentsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await PinAsync(conn, TenantA);

        await using (var tokens = conn.CreateCommand())
        {
            tokens.CommandText = "SELECT id, grant_id, revoked_at FROM oauth_refresh_tokens;";
            await using var reader = await tokens.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                TokensAfterUp[reader.GetGuid(0)] = (reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2));
        }

        await using var devices = conn.CreateCommand();
        devices.CommandText = "SELECT id, grant_id FROM client_devices;";
        await using var deviceReader = await devices.ExecuteReaderAsync();
        while (await deviceReader.ReadAsync())
            DevicesAfterUp[deviceReader.GetGuid(0)] = deviceReader.GetGuid(1);
    }

    private static async Task PinAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var pin = conn.CreateCommand();
        pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        pin.Parameters.AddWithValue("tenant", tenant.ToString());
        await pin.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class KeyOAuthGrantsOnClientAndSubjectTests(KeyOAuthGrantsOnClientAndSubjectFixture fixture)
    : IClassFixture<KeyOAuthGrantsOnClientAndSubjectFixture>
{
    private static IEnumerable<Guid> Revoked(Dictionary<Guid, DateTime?> grants) =>
        grants.Where(g => g.Value is not null).Select(g => g.Key);

    [Fact]
    public void The_seed_holds_the_duplicates_the_migration_must_revoke()
    {
        var active = fixture.RevokedBeforeUp.Where(g => g.Value is null).Select(g => g.Key);

        active.Should().Contain([
            KeyOAuthGrantsOnClientAndSubjectFixture.OldestInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.MiddleInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.NewestInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.TiedLowerIdInB,
            KeyOAuthGrantsOnClientAndSubjectFixture.TiedHigherIdInB,
        ]);
    }

    [Fact]
    public void Each_tenant_keeps_the_grant_created_last_and_revokes_the_others_in_place()
    {
        Revoked(fixture.RevokedAfterUp).Should().BeEquivalentTo([
            KeyOAuthGrantsOnClientAndSubjectFixture.OldestInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.MiddleInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.RevokedBeforeInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.TiedLowerIdInB,
        ]);
        fixture.RevokedAfterUp.Should().HaveCount(fixture.RevokedBeforeUp.Count);
    }

    [Fact]
    public void A_tie_on_creation_time_keeps_the_highest_id()
    {
        fixture.RevokedAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.TiedHigherIdInB].Should().BeNull();
        fixture.RevokedAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.TiedLowerIdInB].Should().NotBeNull();
    }

    [Fact]
    public void A_grant_revoked_earlier_keeps_the_time_it_was_revoked()
    {
        fixture.RevokedAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.RevokedBeforeInA]
            .Should().Be(KeyOAuthGrantsOnClientAndSubjectFixture.RevokedEarlier);
    }

    [Fact]
    public void Grants_that_do_not_share_a_key_and_grants_of_other_types_are_left_unrevoked()
    {
        fixture.RevokedAfterUp.Where(g => g.Value is null).Select(g => g.Key).Should().BeEquivalentTo([
            KeyOAuthGrantsOnClientAndSubjectFixture.NewestInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.OtherClientGrantInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.OtherSubjectGrantInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.DirectInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.OtherDirectInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.GuestInA,
            KeyOAuthGrantsOnClientAndSubjectFixture.TiedHigherIdInB,
        ]);
    }

    [Fact]
    public void A_losing_grants_live_refresh_token_moves_to_the_winner_unrevoked()
    {
        var winner = KeyOAuthGrantsOnClientAndSubjectFixture.NewestInA;

        fixture.TokensAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.TokenOfLoser].Should().Be((winner, (DateTime?)null));
        fixture.TokensAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.TokenOfMiddle].Should().Be((winner, (DateTime?)null));
        fixture.TokensAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.TokenOfWinner].Should().Be((winner, (DateTime?)null));
    }

    [Fact]
    public void A_losing_grants_revoked_refresh_token_moves_to_the_winner_and_stays_revoked()
    {
        fixture.TokensAfterUp[KeyOAuthGrantsOnClientAndSubjectFixture.RevokedTokenOfLoser].Should().Be((
            KeyOAuthGrantsOnClientAndSubjectFixture.NewestInA,
            (DateTime?)KeyOAuthGrantsOnClientAndSubjectFixture.TokenRevokedEarlier));
    }

    [Fact]
    public void A_losing_grants_devices_move_to_the_winner_and_none_is_removed()
    {
        var winner = KeyOAuthGrantsOnClientAndSubjectFixture.NewestInA;

        fixture.DevicesAfterUp.Should().BeEquivalentTo(new Dictionary<Guid, Guid>
        {
            [KeyOAuthGrantsOnClientAndSubjectFixture.DeviceOfLoser] = winner,
            [KeyOAuthGrantsOnClientAndSubjectFixture.DeviceOfMiddle] = winner,
            [KeyOAuthGrantsOnClientAndSubjectFixture.DeviceOfWinner] = winner,
        });
    }

    [Fact]
    public async Task The_key_is_a_valid_partial_unique_index_over_tenant_client_and_subject()
    {
        var definition = (string?)await NpgsqlTestCommands.ScalarAsync<object?>(fixture.UpMigrator, """
            SELECT pg_get_indexdef(i.indexrelid)
            FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = 'ix_oauth_grants_tenant_client_subject_active' AND i.indisunique AND i.indisvalid
            """);

        definition.Should().NotBeNull()
            .And.Contain("(tenant_id, client_id, subject_id)")
            .And.Contain("grant_type")
            .And.Contain("revoked_at IS NULL");
    }

    [Fact]
    public async Task A_second_active_app_grant_is_refused_in_each_tenant_but_a_revoked_one_and_a_guest_grant_are_not()
    {
        var a = KeyOAuthGrantsOnClientAndSubjectFixture.TenantA;
        var b = KeyOAuthGrantsOnClientAndSubjectFixture.TenantB;
        var clientInA = KeyOAuthGrantsOnClientAndSubjectFixture.ClientInA;
        var up = fixture.UpMigrator;

        (await KeyOAuthGrantsOnClientAndSubjectFixture.InsertSqlStateAsync(up, a, clientInA, "app", null))
            .Should().Be(PostgresErrorCodes.UniqueViolation);
        (await KeyOAuthGrantsOnClientAndSubjectFixture.InsertSqlStateAsync(
            up, a, clientInA, "app", DateTime.UtcNow)).Should().BeNull();
        (await KeyOAuthGrantsOnClientAndSubjectFixture.InsertSqlStateAsync(up, a, clientInA, "guest", null))
            .Should().BeNull();
        (await KeyOAuthGrantsOnClientAndSubjectFixture.InsertSqlStateAsync(
            up, b, KeyOAuthGrantsOnClientAndSubjectFixture.ClientInB, "app", null))
            .Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Down_drops_the_unique_index_and_keeps_the_rows()
    {
        fixture.GrantsAfterDown.Select(g => (g.Key, Revoked: g.Value is not null))
            .Should().BeEquivalentTo(fixture.RevokedAfterUp.Select(g => (g.Key, Revoked: g.Value is not null)));
        (await NpgsqlTestCommands.ScalarAsync<object?>(fixture.DownMigrator,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_oauth_grants_tenant_client_subject_active'"))
            .Should().Be(0L);
        (await KeyOAuthGrantsOnClientAndSubjectFixture.InsertSqlStateAsync(
            fixture.DownMigrator,
            KeyOAuthGrantsOnClientAndSubjectFixture.TenantA,
            KeyOAuthGrantsOnClientAndSubjectFixture.ClientInA,
            "app",
            null)).Should().BeNull();
    }
}
