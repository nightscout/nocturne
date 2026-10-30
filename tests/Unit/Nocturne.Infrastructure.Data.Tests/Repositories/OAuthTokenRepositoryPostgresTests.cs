using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="OAuthTokenRepository.RevokeRefreshTokensOfRevokedGrantsAsync"/> on PostgreSQL under
/// the runtime role and Row Level Security: the one bulk update that joins the grant, pinned to a
/// tenant, which SQLite runs as a different statement.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthTokenRepositoryPostgresTests(OAuthTokenRepositoryPostgresTests.Database database)
    : IClassFixture<OAuthTokenRepositoryPostgresTests.Database>
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("oauth_token_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : SingleReaderDbContextFactory
    {
        public override ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(create(scope));
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null))
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private sealed record Seeded(Guid RevokedGrantToken, Guid LiveGrantToken, Guid RevokedGrant, Guid LiveGrant);

    /// <summary>A tenant with a revoked grant and a live grant, each holding one live refresh token.</summary>
    private async Task<(Guid Tenant, Seeded Seeded)> SeedTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'oauth-token-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var subject = Guid.CreateVersion7();
        var revokedGrant = Guid.CreateVersion7();
        var liveGrant = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.Subjects.Add(new SubjectEntity { Id = subject, Name = slug });
            db.OAuthGrants.Add(Grant(revokedGrant, tenant, subject, revokedAt: Now.AddMinutes(-1)));
            db.OAuthGrants.Add(Grant(liveGrant, tenant, subject, revokedAt: null));
            await db.SaveChangesAsync();
        }

        var repo = new OAuthTokenRepository(new Factory(Context, tenant));
        var revokedGrantToken = await repo.StoreRefreshTokenAsync(
            new NewOAuthRefreshToken(tenant, revokedGrant, $"{slug}-revoked", Now.AddDays(90)), CancellationToken.None);
        var liveGrantToken = await repo.StoreRefreshTokenAsync(
            new NewOAuthRefreshToken(tenant, liveGrant, $"{slug}-live", Now.AddDays(90)), CancellationToken.None);
        return (tenant, new Seeded(revokedGrantToken, liveGrantToken, revokedGrant, liveGrant));
    }

    private static OAuthGrantEntity Grant(Guid id, Guid tenant, Guid subject, DateTime? revokedAt) => new()
    {
        Id = id,
        TenantId = tenant,
        SubjectId = subject,
        GrantType = "app",
        Scopes = ["glucose.read"],
        RevokedAt = revokedAt,
    };

    private async Task<DateTime?> RevokedAtAsync(Guid tenant, Guid token)
    {
        await using var db = Context(tenant);
        return await db.OAuthRefreshTokens.Where(t => t.Id == token).Select(t => t.RevokedAt).SingleAsync();
    }

    [Fact]
    public async Task PinnedToATenant_RevokesOnlyThatTenantsTokensOfRevokedGrants()
    {
        var (tenantA, a) = await SeedTenantAsync("a");
        var (tenantB, b) = await SeedTenantAsync("b");

        var revoked = await new OAuthTokenRepository(new Factory(Context, tenantB))
            .RevokeRefreshTokensOfRevokedGrantsAsync(
                tenantA, [a.RevokedGrant, a.LiveGrant, b.RevokedGrant, b.LiveGrant], Now, CancellationToken.None);

        revoked.Should().Be(1);
        (await RevokedAtAsync(tenantA, a.RevokedGrantToken)).Should().Be(Now);
        (await RevokedAtAsync(tenantA, a.LiveGrantToken)).Should().BeNull();
        (await RevokedAtAsync(tenantB, b.RevokedGrantToken)).Should().BeNull();
        (await RevokedAtAsync(tenantB, b.LiveGrantToken)).Should().BeNull();
    }
}
