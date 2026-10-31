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
/// The sweep of <see cref="OAuthClientRepository"/> on PostgreSQL under the runtime role and Row
/// Level Security: a sweep whose scope reads as no one, or as another tenant, deletes the pinned
/// tenant's unreferenced clients and keeps the one a grant names and every other tenant's.
/// </summary>
[Trait("Category", "Integration")]
public class OAuthClientRepositoryPostgresTests(OAuthClientRepositoryPostgresTests.Database database)
    : IClassFixture<OAuthClientRepositoryPostgresTests.Database>
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-2);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("oauth_client_repo");
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

    private sealed record Seeded(Guid Tenant, string Referenced, string Unreferenced);

    /// <summary>A tenant with an old client a grant names and an old client nothing names.</summary>
    private async Task<Seeded> SeedTenantAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'oauth-client-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var clients = new OAuthClientRepository(new Factory(Context, tenant));
        var referenced = await clients.CreateAsync(Client($"{slug}-referenced-{tenant:N}"), CancellationToken.None);
        var unreferenced = await clients.CreateAsync(Client($"{slug}-unreferenced-{tenant:N}"), CancellationToken.None);

        var subject = Guid.CreateVersion7();
        await using (var db = Context(tenant))
        {
            db.Subjects.Add(new SubjectEntity { Id = subject, Name = slug });
            db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject });
            await db.SaveChangesAsync();
            await db.OAuthClients.ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, Old));
        }

        await new OAuthGrantRepository(new Factory(Context, tenant)).CreateAsync(
            new NewOAuthGrant(referenced.Id, subject, OAuthGrantTypes.App, [Scope.GlucoseRead], null, null, null, null, false),
            CancellationToken.None);
        return new Seeded(tenant, referenced.ClientId, unreferenced.ClientId);
    }

    private static NewOAuthClient Client(string clientId) =>
        new(clientId, null, clientId, null, null, clientId, false, ["https://app.example/cb"]);

    private async Task<List<string>> ClientIdsAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        return await db.OAuthClients.OrderBy(c => c.ClientId).Select(c => c.ClientId).ToListAsync();
    }

    [Fact]
    public async Task APinnedSweep_DeletesTheTenantsUnreferencedClientOnly()
    {
        var a = await SeedTenantAsync("a");
        var b = await SeedTenantAsync("b");
        var readingAsNoOne = new OAuthClientRepository(new Factory(Context, Guid.Empty));
        var readingAsB = new OAuthClientRepository(new Factory(Context, b.Tenant));

        (await readingAsNoOne.FindByClientIdAsync(a.Unreferenced, CancellationToken.None))
            .Should().BeNull("an unpinned reader sees no tenant's clients");

        (await readingAsNoOne.DeleteUnreferencedAsync(a.Tenant, Now, "dev.example.docs", CancellationToken.None)).Should().Be(1);
        (await readingAsB.DeleteUnreferencedAsync(a.Tenant, Now, "dev.example.docs", CancellationToken.None)).Should().Be(0);

        (await ClientIdsAsync(a.Tenant)).Should().Equal(a.Referenced);
        (await ClientIdsAsync(b.Tenant)).Should().BeEquivalentTo([b.Referenced, b.Unreferenced]);
    }
}
