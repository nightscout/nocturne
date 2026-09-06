using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies that <see cref="TenantResolutionMiddleware"/> pins the resolved tenant onto the
/// request-scoped <see cref="NocturneDbContext"/>. The scoped context is pool-leased and its
/// <c>TenantId</c> is a custom property that pooling does not reset, so without this pin an
/// unauthenticated request (e.g. the setup/onboarding flow, which has no auth handler to set the
/// tenant) would read and write under a previous lessee's <em>stale</em> tenant — the root cause
/// of an onboarding migration importing one tenant's data into another's.
/// </summary>
public sealed class TenantResolutionMiddlewareTenantPinTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly ServiceProvider _root;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private const string Slug = "acme";
    private const string BaseDomain = "nocturne.run";

    public TenantResolutionMiddlewareTenantPinTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        var services = new ServiceCollection();
        // Mirror production: one scoped context shared by every service injected within the
        // request scope.
        _db.AddToServices(services);
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddMemoryCache();
        _root = services.BuildServiceProvider();

        using var seed = _db.CreateContext();
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = Slug, DisplayName = "Acme" });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _root.Dispose();
        _db.Dispose();
    }

    private TenantResolutionMiddleware Build(RequestDelegate next) => new(
        next,
        NullLogger<TenantResolutionMiddleware>.Instance,
        Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
        _root.GetRequiredService<IMemoryCache>());

    [Fact]
    public async Task Resolving_a_tenant_pins_it_onto_the_scoped_DbContext_overwriting_a_stale_value()
    {
        using var scope = _root.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        // Simulate a pooled context that arrived carrying a previous (different) tenant's id.
        var staleTenant = Guid.CreateVersion7();
        scoped.TenantId = staleTenant;

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers["X-Forwarded-Host"] = $"{Slug}.{BaseDomain}";
        ctx.Request.Path = "/api/v4/migration/start-from-connector/nightscout";

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        // The very instance downstream services inject is now scoped to the resolved tenant,
        // not the stale pooled value — so RLS (and the connector-config read) run under "acme".
        scoped.TenantId.Should().Be(_tenantId);
        scoped.TenantId.Should().NotBe(staleTenant);
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Unknown_subdomain_short_circuits_with_404_and_never_pins()
    {
        using var scope = _root.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var staleTenant = Guid.CreateVersion7();
        scoped.TenantId = staleTenant;

        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers["X-Forwarded-Host"] = $"nope.{BaseDomain}";
        ctx.Request.Path = "/api/v4/migration/history";

        await mw.InvokeAsync(ctx);

        // An unresolvable subdomain is rejected before reaching any controller, so no stale
        // tenant can be acted upon.
        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
