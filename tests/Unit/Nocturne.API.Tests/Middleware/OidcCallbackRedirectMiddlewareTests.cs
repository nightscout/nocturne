using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class OidcCallbackRedirectMiddlewareTests
{
    private readonly BaseDomainOptions _config = new() { BaseDomain = "nocturne.run" };
    private readonly Mock<IOidcAuthService> _oidcAuthService = new();

    private OidcCallbackRedirectMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new OidcCallbackRedirectMiddleware(
            next,
            NullLogger<OidcCallbackRedirectMiddleware>.Instance,
            Options.Create(_config));
    }

    /// <summary>
    /// Returns an opaque state token that the auth service resolves to <paramref name="tenantSlug"/>.
    /// The real state is data-protected, so the middleware cannot read it directly — it asks the
    /// service, and these tests stub that. A token with no mapping resolves to <c>null</c>, which
    /// is what the service returns for a state this instance did not issue.
    /// </summary>
    private string StateFor(string? tenantSlug)
    {
        var token = Guid.NewGuid().ToString("N");
        _oidcAuthService.Setup(s => s.TryReadTenantSlug(token)).Returns(tenantSlug);
        return token;
    }

    private DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_oidcAuthService.Object);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    [Fact]
    public async Task Redirects_apex_oidc_callback_to_tenant_subdomain()
    {
        var middleware = CreateMiddleware();
        var state = StateFor("ryceg");
        var context = NewContext();
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
        var state = StateFor("alice");
        var context = NewContext();
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
        var state = StateFor("ryceg");
        var context = NewContext();
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
        var context = NewContext();
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
        var state = StateFor(null);
        var context = NewContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_state_was_not_issued_by_this_instance()
    {
        // No mapping registered: the service refuses to unprotect it and returns null, so the
        // middleware must not derive a redirect target from it.
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = NewContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString("?code=abc&state=eyJUZW5hbnRTbHVnIjoiZXZpbCJ9");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Passes_through_when_multitenancy_not_configured()
    {
        var called = false;
        var config = new BaseDomainOptions { BaseDomain = null };
        var middleware = new OidcCallbackRedirectMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<OidcCallbackRedirectMiddleware>.Instance,
            Options.Create(config));
        var context = NewContext();
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
        var context = NewContext();
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
        var state = StateFor("ryceg");
        var context = NewContext();
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
        var state = StateFor("evil.attacker.com");
        var context = NewContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }
}
