using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies <see cref="TenantResolutionMiddleware"/> resolves the public share host
/// <c>{token}.share.{baseDomain}</c> by token and marks the request read-only-public, while the
/// bare <c>{slug}.{baseDomain}</c> host (including grandfathered hyphenated slugs) keeps resolving
/// normally with no share access.
/// </summary>
public sealed class TenantResolutionMiddlewareShareTokenTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private const string Slug = "acme";
    private const string ShareToken = "k7m2q9x4r3wt";
    private const string BaseDomain = "nocturne.run";

    public TenantResolutionMiddlewareShareTokenTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext());
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddMemoryCache();
        services.AddSingleton<ShareTokenCacheService>();
        _root = services.BuildServiceProvider();

        using var seed = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        seed.Database.EnsureCreated();
        seed.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = Slug,
            DisplayName = "Acme",
            ShareToken = ShareToken,
            ShareTokenSetAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private TenantResolutionMiddleware Build(RequestDelegate next) => new(
        next,
        NullLogger<TenantResolutionMiddleware>.Instance,
        Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
        _root.GetRequiredService<IMemoryCache>());

    private async Task<(DefaultHttpContext Ctx, bool NextCalled)> Invoke(string host, string path = "/api/v4/entries")
    {
        var scope = _root.CreateScope();
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers["X-Forwarded-Host"] = host;
        ctx.Request.Path = path;
        await mw.InvokeAsync(ctx);
        return (ctx, nextCalled);
    }

    [Fact]
    public async Task Valid_share_host_resolves_tenant_and_marks_share_access()
    {
        var (ctx, nextCalled) = await Invoke($"{ShareToken}.share.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ((bool)ctx.Items["ShareAccess"]!).Should().BeTrue();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
        // The RLS carrier is marked pre-auth on both the request context and the pinned DbContext.
        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().IsShare.Should().BeTrue();
        ctx.RequestServices.GetRequiredService<NocturneDbContext>().IsShareContext.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_share_token_returns_404_and_no_share_access()
    {
        var (ctx, nextCalled) = await Invoke($"deadbeef0000.share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
    }

    [Fact]
    public async Task Bare_slug_host_resolves_tenant_without_share_access()
    {
        var (ctx, nextCalled) = await Invoke($"{Slug}.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
        // A bare slug host is not a share: the carrier stays off, so RLS opens for the owner.
        ctx.RequestServices.GetRequiredService<ICategoryReadContext>().IsShare.Should().BeFalse();
        ctx.RequestServices.GetRequiredService<NocturneDbContext>().IsShareContext.Should().BeFalse();
    }

    [Fact]
    public async Task Inactive_tenant_via_share_token_returns_403()
    {
        using (var db = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext())
        {
            db.Tenants.Single(t => t.Id == _tenantId).IsActive = false;
            db.SaveChanges();
        }

        var (ctx, nextCalled) = await Invoke($"{ShareToken}.share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Uppercase_token_in_host_still_resolves()
    {
        // Hostnames are case-insensitive; generated tokens are lowercase, so an upper-cased link must work.
        var (ctx, nextCalled) = await Invoke($"{ShareToken.ToUpperInvariant()}.share.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ((bool)ctx.Items["ShareAccess"]!).Should().BeTrue();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Slug_ending_in_share_is_not_a_share_host()
    {
        // "mathshare" ends in "share" but the share form requires a ".share" label boundary.
        var id = Guid.CreateVersion7();
        using (var db = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext())
        {
            db.Tenants.Add(new TenantEntity { Id = id, Slug = "mathshare", DisplayName = "Math" });
            db.SaveChanges();
        }

        var (ctx, nextCalled) = await Invoke($"mathshare.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.Slug.Should().Be("mathshare");
    }

    [Fact]
    public async Task Bare_share_label_host_returns_404()
    {
        // share.{baseDomain} has no token and "share" is a reserved slug — no tenant, generic 404.
        var (ctx, nextCalled) = await Invoke($"share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
    }

    [Fact]
    public async Task Hyphenated_slug_is_not_parsed_as_a_share_host()
    {
        var hyphenId = Guid.CreateVersion7();
        using (var db = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext())
        {
            db.Tenants.Add(new TenantEntity { Id = hyphenId, Slug = "as-notrune", DisplayName = "AS" });
            db.SaveChanges();
        }

        var (ctx, nextCalled) = await Invoke($"as-notrune.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.Slug.Should().Be("as-notrune");
    }
}
