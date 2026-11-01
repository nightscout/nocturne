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
/// The pinned reads of <see cref="OAuthGrantRepository"/> on PostgreSQL under the runtime role and
/// Row Level Security: a handler whose scope reads as another tenant, or as no one, reaches the
/// grant of the tenant it pins and nothing of any other, with the membership joined under the
/// <c>subject_pin</c> policy; and a revoke on a unit of work lands the grant with its tokens.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthGrantRepositoryPostgresTests(OAuthGrantRepositoryPostgresTests.Database database)
    : IClassFixture<OAuthGrantRepositoryPostgresTests.Database>
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("oauth_grant_repo");
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

    private sealed record Seeded(Guid Tenant, Guid Subject, Guid Grant, string TokenHash);

    /// <summary>A tenant with a member holding one live direct grant.</summary>
    private async Task<Seeded> SeedTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'oauth-grant-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var subject = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.Subjects.Add(new SubjectEntity { Id = subject, Name = slug });
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject, DirectPermissions = [Scope.GlucoseRead],
            });
            await db.SaveChangesAsync();
        }

        var hash = $"{slug}-{tenant:N}";
        var grant = await new OAuthGrantRepository(new Factory(Context, tenant)).CreateAsync(
            new NewOAuthGrant(null, subject, OAuthGrantTypes.Direct, [Scope.GlucoseRead], slug, hash, null, null, false),
            CancellationToken.None);
        return new Seeded(tenant, subject, grant, hash);
    }

    [Fact]
    public async Task PinnedToATenant_ReadsItsGrantWithTheMembershipAndNoOtherTenants()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var readingAsB = new OAuthGrantRepository(new Factory(Context, b.Tenant));
        var readingAsNoOne = new OAuthGrantRepository(new Factory(Context, Guid.Empty));

        var found = await readingAsB.FindLiveDirectGrantAsync(
            a.Tenant, new DirectGrantLookup.ByTokenHash([a.TokenHash]), Now, CancellationToken.None);
        found!.Grant.Id.Should().Be(a.Grant);
        found.Membership!.Access.EffectivePermissions.Should().Equal(Scope.GlucoseRead);

        (await readingAsNoOne.FindLiveDirectGrantAsync(
            a.Tenant, new DirectGrantLookup.ByTokenHash([a.TokenHash]), Now, CancellationToken.None))!.Grant.Id.Should().Be(a.Grant);
        (await readingAsB.FindLiveDirectGrantAsync(
            b.Tenant, new DirectGrantLookup.ByTokenHash([a.TokenHash]), Now, CancellationToken.None)).Should().BeNull();
        (await readingAsNoOne.GetStateAsync(a.Tenant, a.Grant, CancellationToken.None)).Should().NotBeNull();
        (await readingAsNoOne.GetStateAsync(b.Tenant, a.Grant, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ARevokeOnAPinnedUnit_LandsTheGrantWithItsTokens()
    {
        var a = await SeedTenantAsync("c");
        var token = await new OAuthTokenRepository(new Factory(Context, a.Tenant)).StoreRefreshTokenAsync(
            new NewOAuthRefreshToken(a.Tenant, a.Grant, "c-token", Now.AddDays(90)), CancellationToken.None);

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(new Factory(Context, Guid.Empty), a.Tenant))
        {
            await unit.CommitAsync(async ct =>
            {
                (await new OAuthGrantRepository(unit).RevokeAsync([a.Grant], Now, ct)).Should().Be(1);
                (await new OAuthTokenRepository(unit).RevokeRefreshTokensOfGrantsAsync([a.Grant], Now, ct)).Should().Be(1);
                await unit.Context.SaveChangesAsync(ct);
            });
        }

        await using var db = Context(a.Tenant);
        (await db.OAuthGrants.Where(g => g.Id == a.Grant).Select(g => g.RevokedAt).SingleAsync()).Should().Be(Now);
        (await db.OAuthRefreshTokens.Where(t => t.Id == token).Select(t => t.RevokedAt).SingleAsync()).Should().Be(Now);
    }
}
