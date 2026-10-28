using Microsoft.Extensions.Time.Testing;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The refresh-token queries that compare against the present compare against the injected clock,
/// not the database's <c>now()</c>, so an expiry is decided at the instant the clock gives.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class RefreshTokenClockPostgresTests(RlsCompletenessFixture fx)
{
    private static readonly DateTimeOffset Instant = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetActiveSessionsAsync_DecidesExpiryAtTheInjectedInstant()
    {
        var subject = await SeedSubjectAsync();
        await using var conn = await fx.OpenAppConnectionAsync();
        var live = await SeedTokenAsync(conn, subject, Instant.UtcDateTime.AddMinutes(1));
        await SeedTokenAsync(conn, subject, Instant.UtcDateTime.AddMinutes(-1));

        var sessions = await Repository(conn).GetActiveSessionsAsync(subject);

        sessions.Select(s => s.Id).Should().Equal([live],
            "a token expiring a minute after the clock's instant is live, and one a minute before it is not, "
            + "whatever the database's own time");
    }

    [Fact]
    public async Task UpdateLastUsedAsync_StampsTheInjectedInstant()
    {
        var subject = await SeedSubjectAsync();
        await using var conn = await fx.OpenAppConnectionAsync();
        await SeedTokenAsync(conn, subject, Instant.UtcDateTime.AddDays(1), hash: "last-used");

        await Repository(conn).UpdateLastUsedAsync("last-used");

        await using var context = Context(conn);
        var token = await context.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == "last-used");
        token.LastUsedAt.Should().Be(Instant.UtcDateTime);
        token.UpdatedAt.Should().Be(Instant.UtcDateTime);
    }

    private static EfFirstPartyTokenRepository Repository(NpgsqlConnection conn) =>
        new(new TestNocturneDbContextFactory(Context(conn)), new FakeTimeProvider(Instant));

    private static NocturneDbContext Context(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options);

    private async Task<Guid> SeedSubjectAsync()
    {
        var subject = Guid.CreateVersion7();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, @name, true, false, now(), now(), 'Approved')
            """;
        cmd.Parameters.AddWithValue("id", subject);
        cmd.Parameters.AddWithValue("name", $"clock-{subject:N}");
        await cmd.ExecuteNonQueryAsync();
        return subject;
    }

    private static async Task<Guid> SeedTokenAsync(NpgsqlConnection conn, Guid subject, DateTime expiresAt, string? hash = null)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
            VALUES (@id, @hash, @subject, @issued, @expires, @issued, @issued)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hash", hash ?? $"{id:N}");
        cmd.Parameters.AddWithValue("subject", subject);
        cmd.Parameters.AddWithValue("issued", Instant.UtcDateTime.AddDays(-1));
        cmd.Parameters.AddWithValue("expires", expiresAt);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }
}
