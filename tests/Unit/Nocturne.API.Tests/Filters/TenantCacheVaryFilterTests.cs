using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Nocturne.API.Filters;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Filters;

public class TenantCacheVaryFilterTests
{
    private static readonly TenantContext TestTenant = new(Guid.CreateVersion7(), "test", "Test Tenant", true);

    private static async Task<HttpContext> RunAsync(
        TenantContext? tenant,
        string? cacheControl,
        string? existingVary = null)
    {
        var httpContext = new DefaultHttpContext();
        if (tenant != null)
            httpContext.Items["TenantContext"] = tenant;
        if (cacheControl != null)
            httpContext.Response.Headers.CacheControl = cacheControl;
        if (existingVary != null)
            httpContext.Response.Headers.Vary = existingVary;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ResultExecutingContext(
            actionContext, new List<IFilterMetadata>(), new OkResult(), controller: null!);
        ResultExecutionDelegate next = () => Task.FromResult(
            new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), new OkResult(), controller: null!));

        await new TenantCacheVaryFilter().OnResultExecutionAsync(executingContext, next);
        return httpContext;
    }

    [Fact]
    public async Task AddsVaryCookie_ForPublicTenantScopedResponse()
    {
        var http = await RunAsync(TestTenant, "public,max-age=60");
        http.Response.Headers.Vary.ToString().Should().Contain("Cookie");
    }

    [Fact]
    public async Task DoesNotAddVary_WhenNoTenantResolved()
    {
        var http = await RunAsync(tenant: null, "public,max-age=60");
        http.Response.Headers.Vary.ToString().Should().NotContain("Cookie");
    }

    [Fact]
    public async Task DoesNotAddVary_WhenResponseNotPublic()
    {
        var http = await RunAsync(TestTenant, "private,max-age=60");
        http.Response.Headers.Vary.ToString().Should().NotContain("Cookie");
    }

    [Fact]
    public async Task DoesNotAddVary_WhenNoCacheControl()
    {
        var http = await RunAsync(TestTenant, cacheControl: null);
        http.Response.Headers.Vary.ToString().Should().NotContain("Cookie");
    }

    [Fact]
    public async Task DoesNotDuplicateCookie_WhenAlreadyPresent()
    {
        var http = await RunAsync(TestTenant, "public,max-age=60", existingVary: "Cookie");
        http.Response.Headers.Vary.Count(v => v != null && v.Contains("Cookie")).Should().Be(1);
    }

    [Fact]
    public async Task PreservesExistingVary_WhenAddingCookie()
    {
        var http = await RunAsync(TestTenant, "public,max-age=60", existingVary: "If-Modified-Since");
        var vary = http.Response.Headers.Vary.ToString();
        vary.Should().Contain("If-Modified-Since");
        vary.Should().Contain("Cookie");
    }
}
