using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

public class ApexCallbackRelayMiddlewareTests
{
    private readonly BaseDomainOptions _config = new() { BaseDomain = "nocturne.run" };
    private readonly Mock<IOidcAuthService> _oidcAuthService = new();
    private readonly GoogleHealthOAuthState _googleHealthState = new(new EphemeralDataProtectionProvider());

    private ApexCallbackRelayMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new ApexCallbackRelayMiddleware(
            next,
            NullLogger<ApexCallbackRelayMiddleware>.Instance,
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
        services.AddSingleton(_googleHealthState);
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
        var config = new BaseDomainOptions { BaseDomain = "" };
        var middleware = new ApexCallbackRelayMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<ApexCallbackRelayMiddleware>.Instance,
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
    public async Task Stray_forwarded_host_header_does_not_stand_in_for_a_subdomain()
    {
        var middleware = CreateMiddleware();
        var state = StateFor("ryceg");
        var context = NewContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Headers["X-Forwarded-Host"] = "evil.nocturne.run";
        context.Request.Path = "/api/auth/oidc/link/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        context.Response.Headers.Location.ToString()
            .Should().StartWith("https://ryceg.nocturne.run/api/auth/oidc/link/callback?");
    }

    /// <summary>
    /// Local development runs the whole stack over plain http behind a gateway that forwards
    /// <c>X-Forwarded-Proto: http</c>, so an https redirect here points at a port nothing serves.
    /// </summary>
    [Fact]
    public async Task Redirect_keeps_http_when_that_is_what_the_caller_used()
    {
        var middleware = CreateMiddleware();
        var state = StateFor("ryceg");
        var context = NewContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        context.Response.Headers.Location.ToString().Should().StartWith("http://ryceg.nocturne.run/");
    }

    [Fact]
    public async Task Redirect_falls_back_to_https_for_a_scheme_no_browser_would_follow()
    {
        var middleware = CreateMiddleware();
        var state = StateFor("ryceg");
        var context = NewContext();
        context.Request.Scheme = "javascript";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = "/api/auth/oidc/callback";
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        context.Response.Headers.Location.ToString().Should().StartWith("https://ryceg.nocturne.run/");
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

    [Fact]
    public async Task Relays_apex_google_health_callback_to_the_tenant_its_state_names()
    {
        var middleware = CreateMiddleware();
        var state = _googleHealthState.Protect("alice", DateTimeOffset.UtcNow.AddMinutes(10));
        var context = NewContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = new QueryString($"?code=abc&state={state}");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(302);
        context.Response.Headers.Location.ToString()
            .Should().Be($"https://alice.nocturne.run{GoogleHealthPlatformOptions.CallbackPath}{context.Request.QueryString}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("evil.attacker.com")]
    public async Task Google_health_callback_without_a_routable_slug_stays_on_the_apex(string? slug)
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = NewContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = QueryString.Create(
            "state", _googleHealthState.Protect(slug, DateTimeOffset.UtcNow.AddMinutes(10)));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Google_health_callback_with_an_expired_state_stays_on_the_apex()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = NewContext();
        context.Request.Host = new HostString("nocturne.run");
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = QueryString.Create(
            "state", _googleHealthState.Protect("alice", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Google_health_callback_on_a_tenant_host_passes_through()
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = NewContext();
        context.Request.Host = new HostString("alice.nocturne.run");
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = QueryString.Create(
            "state", _googleHealthState.Protect("bob", DateTimeOffset.UtcNow.AddMinutes(10)));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Redirect_keeps_the_port_the_browser_reached_the_apex_on()
    {
        _config.BaseDomain = "nocturne.localhost:1631";
        var middleware = CreateMiddleware();
        var state = _googleHealthState.Protect("alice", DateTimeOffset.UtcNow.AddMinutes(10));
        var context = NewContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("nocturne.localhost", 1612);
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = QueryString.Create("state", state);

        await middleware.InvokeAsync(context);

        context.Response.Headers.Location.ToString()
            .Should().StartWith($"https://alice.nocturne.localhost:1612{GoogleHealthPlatformOptions.CallbackPath}?");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99999)]
    public async Task Passes_through_when_the_request_port_is_out_of_range(int port)
    {
        var called = false;
        var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = NewContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString($"nocturne.run:{port}");
        context.Request.Path = GoogleHealthPlatformOptions.CallbackPath;
        context.Request.QueryString = QueryString.Create(
            "state", _googleHealthState.Protect("alice", DateTimeOffset.UtcNow.AddMinutes(10)));

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
    }
}
