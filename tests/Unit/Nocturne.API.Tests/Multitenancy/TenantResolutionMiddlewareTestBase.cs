using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// The request scope <see cref="TenantResolutionMiddleware"/> runs in: a SQLite-backed context
/// factory, the scoped context the middleware pins the tenant onto, and the accessors downstream
/// services read.
/// </summary>
/// <remarks>
/// Request scopes outlive the call that made them so a test can read what the middleware left on
/// them; the provider disposes them at teardown.
/// </remarks>
public abstract class TenantResolutionMiddlewareTestBase : IDisposable
{
    private readonly SqliteTestDatabase _db;

    protected TenantResolutionMiddlewareTestBase()
    {
        _db = TestDbContextFactory.CreateSqlite();

        var services = new ServiceCollection();
        _db.AddToServices(services);
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddSingleton<ShareTokenCacheService>();
        services.AddMemoryCache();
        services.AddLogging();
        Root = services.BuildServiceProvider();
    }

    protected ServiceProvider Root { get; }

    /// <summary>The suffix tenant subdomains hang off, as the deployment configures it.</summary>
    protected virtual string BaseDomain => "nocturne.run";

    public void Dispose()
    {
        Root.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    protected NocturneDbContext Db() =>
        Root.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContext();

    protected Guid SeedTenant(
        string slug, bool isActive = true, bool isDemo = false, Guid? id = null) =>
        SeedTenant(new TenantEntity
        {
            Id = id ?? Guid.Empty,
            Slug = slug,
            DisplayName = slug,
            IsActive = isActive,
            IsDemo = isDemo,
        });

    protected Guid SeedTenant(TenantEntity tenant)
    {
        tenant.Id = tenant.Id == Guid.Empty ? Guid.CreateVersion7() : tenant.Id;
        using var db = Db();
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    protected TenantResolutionMiddleware Build(RequestDelegate next) => new(
        next,
        NullLogger<TenantResolutionMiddleware>.Instance,
        Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
        Root.GetRequiredService<IMemoryCache>());

    protected Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(
        string host,
        string path = "/api/v4/entries",
        string method = "GET",
        Action<HttpRequest>? configure = null) =>
        InvokeAsync(Root.CreateScope(), host, path, method, configure);

    protected async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(
        IServiceScope scope,
        string host,
        string path = "/api/v4/entries",
        string method = "GET",
        Action<HttpRequest>? configure = null)
    {
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        configure?.Invoke(context.Request);

        var nextCalled = false;
        await Build(_ => { nextCalled = true; return Task.CompletedTask; }).InvokeAsync(context);
        return (context, nextCalled);
    }

    protected static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    protected static T Resolve<T>(HttpContext context) where T : notnull =>
        context.RequestServices.GetRequiredService<T>();
}
