using System.Linq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Security;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

public sealed class ShareTokenCacheServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly IMemoryCache _cache;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private const string Token = "k7m2q9x4r3wt";

    public ShareTokenCacheServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddMemoryCache();
        _root = services.BuildServiceProvider();
        _factory = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        _cache = _root.GetRequiredService<IMemoryCache>();

        using var seed = _factory.CreateDbContext();
        seed.Database.EnsureCreated();
        seed.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "acme",
            DisplayName = "Acme",
            ShareToken = CredentialHash.ShareToken(Token),
        });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private ShareTokenCacheService Service() =>
        new(_cache, _factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShareTokenCacheService>.Instance);

    [Fact]
    public async Task ResolveByTokenAsync_returns_the_owning_tenant()
    {
        var ctx = await Service().ResolveByTokenAsync(Token);

        ctx.Should().NotBeNull();
        ctx!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task ResolveByTokenAsync_returns_null_for_unknown_token()
    {
        (await Service().ResolveByTokenAsync("nope00000000")).Should().BeNull();
    }

    [Fact]
    public async Task ResolveByTokenAsync_stamps_last_accessed_on_a_database_hit()
    {
        var before = DateTime.UtcNow;

        await Service().ResolveByTokenAsync(Token);

        using var db = _factory.CreateDbContext();
        db.Tenants.Single(t => t.Id == _tenantId).ShareLastAccessedAt
            .Should().NotBeNull().And.BeOnOrAfter(before);
    }

    [Fact]
    public async Task ResolveByTokenAsync_cached_hit_does_not_stamp_again()
    {
        var service = Service();
        await service.ResolveByTokenAsync(Token);
        DateTime? first;
        using (var db = _factory.CreateDbContext())
            first = db.Tenants.Single(t => t.Id == _tenantId).ShareLastAccessedAt;

        await service.ResolveByTokenAsync(Token); // served from cache — no write

        using (var db = _factory.CreateDbContext())
            db.Tenants.Single(t => t.Id == _tenantId).ShareLastAccessedAt.Should().Be(first);
    }

    [Fact]
    public async Task ResolveByTokenAsync_does_not_stamp_an_inactive_tenant()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Tenants.Single(t => t.Id == _tenantId).IsActive = false;
            db.SaveChanges();
        }

        await Service().ResolveByTokenAsync(Token);

        using var check = _factory.CreateDbContext();
        check.Tenants.Single(t => t.Id == _tenantId).ShareLastAccessedAt.Should().BeNull(
            "the middleware rejects inactive-tenant share requests, so nothing was accessed");
    }

    [Fact]
    public async Task ResolveWithoutRecordingAccessAsync_resolves_but_does_not_stamp()
    {
        // The TLS authorization endpoint asks whether to mint a certificate for a share host.
        // That is not somebody opening the link, and the value is shown to the tenant owner as
        // "Last viewed", so a probe must not move it.
        var tenant = await Service().ResolveWithoutRecordingAccessAsync(Token, CancellationToken.None);

        tenant.Should().NotBeNull();

        using var db = _factory.CreateDbContext();
        db.Tenants.Single(t => t.Id == _tenantId).ShareLastAccessedAt.Should().BeNull(
            "asking whether to issue a certificate is not a view of the share");
    }

    [Fact]
    public async Task EvictByHash_makes_a_rotated_token_stop_resolving()
    {
        var service = Service();
        (await service.ResolveByTokenAsync(Token)).Should().NotBeNull(); // caches the hit

        // Rotate in the DB, then evict the cached entry keyed by the old token's digest.
        using (var db = _factory.CreateDbContext())
        {
            db.Tenants.Single(t => t.Id == _tenantId).ShareToken =
                CredentialHash.ShareToken("newtoken0000");
            db.SaveChanges();
        }
        service.EvictByHash(CredentialHash.ShareToken(Token));

        (await service.ResolveByTokenAsync(Token)).Should().BeNull();
        (await service.ResolveByTokenAsync("newtoken0000"))!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task ResolveByTokenAsync_does_not_resolve_a_token_stored_as_plaintext()
    {
        using (var db = _factory.CreateDbContext())
        {
            db.Tenants.Single(t => t.Id == _tenantId).ShareToken = Token;
            db.SaveChanges();
        }

        (await Service().ResolveByTokenAsync(Token)).Should().BeNull(
            "lookup is by digest, so a plaintext column value is not a usable credential");
    }

    [Fact]
    public void The_stored_share_token_is_not_the_token()
    {
        using var db = _factory.CreateDbContext();
        var stored = db.Tenants.Single(t => t.Id == _tenantId).ShareToken;

        stored.Should().NotBe(Token);
        stored.Should().HaveLength(CredentialHash.HexLength).And.MatchRegex("^[0-9a-f]+$");
    }
}
