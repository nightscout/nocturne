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
/// Apex (no-subdomain) resolution in <see cref="TenantResolutionMiddleware"/>. Single-tenant
/// installs are served at the base domain itself, so the apex must resolve the sole tenant for
/// tenant-scoped paths — including otherwise-tenantless ones like <c>GET /api/v4/status</c>.
/// Leaving status tenantless on the apex made it report "setup_required", which bounced a fully
/// configured single-tenant install to /setup. Multi-tenant and zero-tenant behaviour is
/// unchanged, and infrastructure/liveness paths stay tenant-agnostic.
/// </summary>
public sealed class TenantResolutionMiddlewareApexTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _root;
    private const string BaseDomain = "nocturne.theconen.de";

    public TenantResolutionMiddlewareApexTests()
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

    private TenantResolutionMiddleware Build(RequestDelegate next) => new(
        next,
        NullLogger<TenantResolutionMiddleware>.Instance,
        Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
        _root.GetRequiredService<IMemoryCache>());

    private Guid SeedTenant(string slug)
    {
        var id = Guid.CreateVersion7();
        using var seed = _root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();
        seed.Tenants.Add(new TenantEntity { Id = id, Slug = slug, DisplayName = slug, IsActive = true });
        seed.SaveChanges();
        return id;
    }

    // Host == BaseDomain → apex, no subdomain.
    private DefaultHttpContext ApexRequest(IServiceScope scope, string path)
    {
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers["X-Forwarded-Host"] = BaseDomain;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task Apex_status_resolves_the_sole_tenant_in_single_tenant_mode()
    {
        var tenantId = SeedTenant("theconen");
        using var scope = _root.CreateScope();

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = ApexRequest(scope, "/api/v4/status");

        await mw.InvokeAsync(ctx);

        // /api/v4/status is tenantless-allowed, but on the apex the sole tenant is resolved
        // so StatusService reports that tenant instead of "setup_required".
        nextCalled.Should().BeTrue();
        var accessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
        accessor.IsResolved.Should().BeTrue();
        accessor.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Apex_health_probe_passes_through_without_resolving_a_tenant()
    {
        SeedTenant("theconen");
        using var scope = _root.CreateScope();

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = ApexRequest(scope, "/health");

        await mw.InvokeAsync(ctx);

        // Liveness/readiness probes must never depend on tenant state — no resolution, no DB touch.
        nextCalled.Should().BeTrue();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_status_with_multiple_tenants_stays_tenantless()
    {
        SeedTenant("alpha");
        SeedTenant("beta");
        using var scope = _root.CreateScope();

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = ApexRequest(scope, "/api/v4/status");

        await mw.InvokeAsync(ctx);

        // With more than one tenant there is no sole tenant; status stays tenantless and passes
        // through, so the apex status response is unchanged for multi-tenant deployments.
        nextCalled.Should().BeTrue();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Apex_non_tenantless_path_with_no_tenants_returns_503_setup_required()
    {
        using var scope = _root.CreateScope();

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = ApexRequest(scope, "/api/v4/entries");

        await mw.InvokeAsync(ctx);

        // Fresh install (zero tenants), non-tenantless path: 503 so the frontend goes to /setup.
        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
