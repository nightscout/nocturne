using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class OidcCallbackRedirectMiddlewareTests
{
    private readonly MultitenancyConfiguration _config = new() { BaseDomain = "nocturne.run" };

    private OidcCallbackRedirectMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new OidcCallbackRedirectMiddleware(
            next,
            NullLogger<OidcCallbackRedirectMiddleware>.Instance,
            Options.Create(_config));
    }

    private static string EncodeState(object data)
    {
        var json = JsonSerializer.Serialize(data);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    [Fact]
    public async Task Redirects_apex_oidc_callback_to_tenant_subdomain()
    {
        var middleware = CreateMiddleware();
        var state = EncodeState(new { TenantSlug = "ryceg" });
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/link/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString()
            .Should().StartWith("https://ryceg.nocturne.run/api/auth/oidc/link/callback?")
            .And.Contain("code=abc")
            .And.Contain($"state={state}");
    }

    [Fact]
    public async Task Redirects_apex_login_callback_to_tenant_subdomain()
    {
        var middleware = CreateMiddleware();
        var state = EncodeState(new { TenantSlug = "alice" });
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=xyz&state={state}");
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString()
            .Should().StartWith("https://alice.nocturne.run/api/auth/oidc/callback?");
    }

    [Fact]
    public async Task Passes_through_when_subdomain_present()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var state = EncodeState(new { TenantSlug = "ryceg" });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("ryceg.nocturne.run");
        context.Request.Path = "/api/auth/oidc/link/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_for_non_oidc_paths()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/v4/entries";

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_state_has_no_tenant_slug()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var state = EncodeState(new { Intent = "login" });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_multitenancy_not_configured()
    {
        var called = false;
        var config = new MultitenancyConfiguration { BaseDomain = null };
        var middleware = new OidcCallbackRedirectMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<OidcCallbackRedirectMiddleware>.Instance,
            Options.Create(config));
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_state_parameter_missing()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString("?code=abc");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Uses_x_forwarded_host_for_subdomain_detection()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var state = EncodeState(new { TenantSlug = "ryceg" });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne-api");
        context.Request.Headers["X-Forwarded-Host"] = "ryceg.nocturne.run";
        context.Request.Path = "/api/auth/oidc/link/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_tenant_slug_contains_invalid_characters()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var state = EncodeState(new { TenantSlug = "evil.attacker.com" });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }
}
