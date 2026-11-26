using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddLoginCodeIdToRefreshTokens</c> on a real PostgreSQL as the migrator: of the refresh
/// tokens stored before it, only a live one redeemed from a login code is revoked, and it is revoked
/// as the application revokes (time, reason, <c>updated_at</c>, no successor).
/// </summary>
public class AddLoginCodeIdToRefreshTokensFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261121125548_AddLoginCodeEmail";
    private const string EarlierRevokedReason = "User logout";

    internal static readonly Guid Subject = Guid.Parse("c3333333-3333-7333-8333-333333333785");
    internal static readonly Guid LiveHandoff = Guid.Parse("d1111111-1111-7111-8111-111111113785");
    internal static readonly Guid LiveOrdinary = Guid.Parse("d2222222-2222-7222-8222-222222223785");
    internal static readonly Guid RevokedHandoff = Guid.Parse("d3333333-3333-7333-8333-333333333785");
    internal static readonly Guid ExpiredHandoff = Guid.Parse("d4444444-4444-7444-8444-444444443785");

    private static readonly DateTime SeededUpdatedAt = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EarlierRevokedAt = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private string _migrator = string.Empty;

    internal DateTime MigrationStartedAt { get; private set; }
    internal Dictionary<Guid, TokenRow> Before { get; private set; } = [];
    internal Dictionary<Guid, TokenRow> After { get; private set; } = [];
    internal int RowsChangedBySecondRun { get; private set; }

    internal sealed record TokenRow(DateTime? RevokedAt, string? RevokedReason, Guid? ReplacedByTokenId, DateTime UpdatedAt);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "login_code_id_refresh_tokens");
        _migrator = database.MigratorConnectionString;

        await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{Subject}', 'handoff-revoke', true, false, now(), now(), 'Approved');
            """);
        await InsertTokenAsync(LiveHandoff, "Login handoff", "now() + interval '5 days'", revoked: false);
        await InsertTokenAsync(LiveOrdinary, "Chrome on Windows", "now() + interval '5 days'", revoked: false);
        await InsertTokenAsync(RevokedHandoff, "Login handoff", "now() + interval '5 days'", revoked: true);
        await InsertTokenAsync(ExpiredHandoff, "Login handoff", "now() - interval '1 day'", revoked: false);

        Before = await ReadTokensAsync();
        MigrationStartedAt = DateTime.UtcNow;

        await using (var context = new NocturneDbContext(Options()))
            await context.GetService<IMigrator>().MigrateAsync();

        After = await ReadTokensAsync();
        RowsChangedBySecondRun = await NpgsqlTestCommands.ExecuteAsync(_migrator, AddLoginCodeIdToRefreshTokens.RevokeUnmarkedHandoffsSql);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<int> InsertTokenAsync(Guid id, string device, string expiresAt, bool revoked) => NpgsqlTestCommands.ExecuteAsync(
        _migrator, $"""
        INSERT INTO refresh_tokens (id, token_hash, subject_id, oidc_session_id, issued_at, expires_at,
                                    revoked_at, revoked_reason, device_description, created_at, updated_at)
        VALUES ('{id}', '{id:N}', '{Subject}', 'session-{id:N}', now() - interval '2 days', {expiresAt},
                {(revoked ? $"'{EarlierRevokedAt:O}'" : "NULL")}, {(revoked ? $"'{EarlierRevokedReason}'" : "NULL")},
                '{device}', '{SeededUpdatedAt:O}', '{SeededUpdatedAt:O}');
        """);

    private DbContextOptions<NocturneDbContext> Options() =>
        TestContexts.MigrationsOptions(_migrator);

    private async Task<Dictionary<Guid, TokenRow>> ReadTokensAsync()
    {
        var rows = new Dictionary<Guid, TokenRow>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, revoked_at, revoked_reason, replaced_by_token_id, updated_at FROM refresh_tokens", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetGuid(0)] = new TokenRow(
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetDateTime(4));
        }

        return rows;
    }
}

[Trait("Category", "Integration")]
public class AddLoginCodeIdToRefreshTokensTests(AddLoginCodeIdToRefreshTokensFixture fixture)
    : IClassFixture<AddLoginCodeIdToRefreshTokensFixture>
{
    [Fact]
    public void All_four_seeded_tokens_survive_the_migration() =>
        fixture.After.Keys.Should().BeEquivalentTo(fixture.Before.Keys).And.HaveCount(4);

    [Fact]
    public void A_live_handed_off_token_is_revoked_as_the_application_revokes()
    {
        var row = fixture.After[AddLoginCodeIdToRefreshTokensFixture.LiveHandoff];

        row.RevokedAt.Should().NotBeNull().And.BeOnOrAfter(fixture.MigrationStartedAt.AddSeconds(-5));
        row.RevokedReason.Should().Be(AddLoginCodeIdToRefreshTokens.RevokedReason);
        row.ReplacedByTokenId.Should().BeNull("a token with a successor is the one reuse detection acts on");
        row.UpdatedAt.Should().Be(row.RevokedAt!.Value);
    }

    [Theory]
    [MemberData(nameof(Untouched))]
    public void Every_other_token_is_unchanged(Guid id) =>
        fixture.After[id].Should().Be(fixture.Before[id]);

    [Fact]
    public void A_second_run_changes_nothing() => fixture.RowsChangedBySecondRun.Should().Be(0);

    public static TheoryData<Guid> Untouched() =>
    [
        AddLoginCodeIdToRefreshTokensFixture.LiveOrdinary,
        AddLoginCodeIdToRefreshTokensFixture.RevokedHandoff,
        AddLoginCodeIdToRefreshTokensFixture.ExpiredHandoff,
    ];
}
