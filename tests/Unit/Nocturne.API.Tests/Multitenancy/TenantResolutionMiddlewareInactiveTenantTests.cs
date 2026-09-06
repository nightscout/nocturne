using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// An inactive tenant's subdomain in <see cref="TenantResolutionMiddleware"/>. The refusal has to
/// name itself, because the person on the other side of it is usually the account holder and the
/// web app can only explain what it can recognise; and it has to leave the liveness probes and the
/// operator's address answering, because a probe cannot otherwise tell a suspended deployment from
/// a broken one and the page explaining the refusal has nowhere to point.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantResolutionMiddlewareInactiveTenantTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly ServiceProvider _root;
    private const string BaseDomain = "nocturne.example";
    private const string Slug = "lapsed";

    public TenantResolutionMiddlewareInactiveTenantTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        var services = new ServiceCollection();
        _db.AddToServices(services);
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddMemoryCache();
        _root = services.BuildServiceProvider();

        using var seed = _db.CreateContext();
        seed.Tenants.Add(new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = Slug,
            DisplayName = Slug,
            IsActive = false,
        });
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

    private static DefaultHttpContext Request(IServiceScope scope, string path, string method = "GET")
    {
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        ctx.Request.Headers["X-Forwarded-Host"] = $"{Slug}.{BaseDomain}";
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private async Task<(bool NextCalled, DefaultHttpContext Context, string Body)> InvokeAsync(
        string path, string method = "GET")
    {
        using var scope = _root.CreateScope();
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = Request(scope, path, method);

        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (nextCalled, ctx, body);
    }

    [Theory]
    [InlineData("/api/v4/sensorglucose")]
    [InlineData("/api/v4/status")]
    [InlineData("/api/auth/oidc/session")]
    public async Task Refuses_with_a_code_the_web_app_can_recognise(string path)
    {
        var (nextCalled, ctx, body) = await InvokeAsync(path);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()
            .Should().Be(TenantResolutionMiddleware.TenantInactiveCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Liveness_probes_answer_without_resolving_the_tenant(string path)
    {
        using var scope = _root.CreateScope();
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = Request(scope, path);

        await mw.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task The_operators_address_is_readable_so_the_refusal_can_be_explained()
    {
        var (nextCalled, ctx, _) = await InvokeAsync("/api/v4/support/config");

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Only_the_method_the_slice_names_is_served()
    {
        var (nextCalled, ctx, _) = await InvokeAsync("/api/v4/support/config", HttpMethods.Post);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The inactive slice is a subset of the tenantless list, not a second list that could drift
    /// into admitting something the tenantless surface itself does not.
    /// </summary>
    [Fact]
    public void Every_inactive_entry_is_also_tenantless_allowed()
    {
        TenantResolutionMiddleware.InactiveTenantPaths.Should().NotBeEmpty();

        foreach (var entry in TenantResolutionMiddleware.InactiveTenantPaths)
        {
            TenantResolutionMiddleware.IsTenantlessAllowed(entry.Path, entry.Method)
                .Should().BeTrue("{0} is served on an inactive tenant's host but is not on the "
                    + "tenantless list it is meant to be a slice of", entry.Path);
        }
    }
}
