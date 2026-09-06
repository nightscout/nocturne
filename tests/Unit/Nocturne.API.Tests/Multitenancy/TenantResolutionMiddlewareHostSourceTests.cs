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
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Which host <see cref="TenantResolutionMiddleware"/> picks the tenant from. The forwarded-headers
/// middleware runs first and applies <c>X-Forwarded-Host</c> onto <see cref="HttpRequest.Host"/>,
/// consuming the entry it took; what it declines to apply — a host that fails its format check, or a
/// header carrying more than the one entry it will consume — it leaves in place. Reading the header
/// here would honour exactly those refusals, giving tenant selection a second host rule the rest of
/// the pipeline does not share.
/// </summary>
public sealed class TenantResolutionMiddlewareHostSourceTests : IDisposable
{
    private const string BaseDomain = "nocturne.run";
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;

    public TenantResolutionMiddlewareHostSourceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext());
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddMemoryCache();
        _root = services.BuildServiceProvider();

        using var seed = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        seed.Database.EnsureCreated();
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _root.Dispose();
        _connection.Dispose();
    }

    private Guid SeedTenant(string slug)
    {
        var id = Guid.CreateVersion7();
        using var seed = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        seed.Tenants.Add(new TenantEntity { Id = id, Slug = slug, DisplayName = slug, IsActive = true });
        seed.SaveChanges();
        return id;
    }

    private async Task<ITenantAccessor> ResolveAsync(string host, string? strayForwardedHost = null)
    {
        var scope = _root.CreateScope();
        var mw = new TenantResolutionMiddleware(
            _ => Task.CompletedTask,
            NullLogger<TenantResolutionMiddleware>.Instance,
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
            _root.GetRequiredService<IMemoryCache>());

        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Host = new HostString(host);
        if (strayForwardedHost != null)
            ctx.Request.Headers["X-Forwarded-Host"] = strayForwardedHost;
        ctx.Request.Path = "/api/v4/entries";
        ctx.Response.Body = new MemoryStream();

        await mw.InvokeAsync(ctx);
        return scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
    }

    [Fact]
    public async Task Host_rewritten_by_the_forwarded_headers_middleware_selects_the_tenant()
    {
        var tenantId = SeedTenant("acme");

        var accessor = await ResolveAsync($"acme.{BaseDomain}:1612");

        accessor.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Forwarded_host_header_left_in_place_does_not_select_the_tenant()
    {
        var acme = SeedTenant("acme");
        SeedTenant("victim");

        var accessor = await ResolveAsync($"acme.{BaseDomain}", $"victim.{BaseDomain}");

        accessor.TenantId.Should().Be(acme);
    }
}
