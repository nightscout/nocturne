using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Extensions;

/// <summary>
/// Covers the session-cookie Domain widening: the value derived from the base domain, that every
/// writer and the deleter agree on it, and that the pre-widening host-scoped cookies are expired
/// so a browser holding both converges on one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SessionCookieExtensionsTests
{
    private const string AccessName = ".Nocturne.AccessToken";
    private const string RefreshName = ".Nocturne.RefreshToken";
    private const string FlagName = "IsAuthenticated";

    private static OidcOptions Options(string? sessionDomain) => new()
    {
        Cookie = new CookieSettings { SessionDomain = sessionDomain, Secure = true },
    };

    private static (HttpResponse Response, Func<IReadOnlyList<string>> Cookies) NewResponse()
    {
        var ctx = new DefaultHttpContext();
        return (ctx.Response, () => ctx.Response.Headers.SetCookie.ToArray()!);
    }

    [Theory]
    // Ports are not part of a Domain attribute; a value carrying one is discarded by the browser.
    [InlineData("nocturne.run", ".nocturne.run")]
    [InlineData("nocturne.run:1612", ".nocturne.run")]
    [InlineData("cgm.example.co.uk", ".cgm.example.co.uk")]
    // Single-label hosts cannot carry a Domain attribute at all.
    [InlineData("localhost", null)]
    [InlineData("localhost:1612", null)]
    // Chromium does not reliably scope cookies across *.localhost names.
    [InlineData("nocturne.localhost", null)]
    [InlineData("nocturne.localhost:1612", null)]
    // An IP literal has no domain hierarchy: a browser discards any cookie carrying a Domain
    // attribute on an IP host, so widening a LAN install's session cookies would lose them all.
    [InlineData("192.168.1.10", null)]
    [InlineData("192.168.1.10:1612", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("[::1]", null)]
    [InlineData("[2001:db8::1]", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveCookieDomain_derives_the_widened_domain(string? baseDomain, string? expected)
    {
        SessionCookieExtensions.ResolveCookieDomain(baseDomain).Should().Be(expected);
    }

    [Fact]
    public void SetSessionCookies_stamps_the_domain_on_every_session_cookie()
    {
        var (response, cookies) = NewResponse();

        response.SetSessionCookies("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5),
            Options(".nocturne.run"));

        foreach (var name in new[] { AccessName, RefreshName, FlagName })
        {
            Written(cookies(), name).Should().Contain("domain=.nocturne.run",
                $"{name} must be presented to every host under the base domain");
        }
    }

    [Fact]
    public void SetSessionCookies_expires_the_host_scoped_cookies_it_replaces()
    {
        var (response, cookies) = NewResponse();

        response.SetSessionCookies("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5),
            Options(".nocturne.run"));

        foreach (var name in new[] { AccessName, RefreshName, FlagName })
        {
            var headers = cookies().Where(c => c.StartsWith($"{name}=", StringComparison.Ordinal)).ToList();
            headers.Should().HaveCount(2,
                $"{name} needs one domain-wide write plus one host-scoped expiry");

            var hostScoped = headers.Single(h => !h.Contains("domain=", StringComparison.OrdinalIgnoreCase));
            hostScoped.Should().Contain("expires=Thu, 01 Jan 1970",
                "the host-scoped variant must be deleted, not merely left behind");
        }
    }

    [Fact]
    public void SetSessionCookies_writes_a_single_host_scoped_cookie_when_not_widened()
    {
        var (response, cookies) = NewResponse();

        response.SetSessionCookies("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5),
            Options(null));

        foreach (var name in new[] { AccessName, RefreshName, FlagName })
        {
            cookies().Count(c => c.StartsWith($"{name}=", StringComparison.Ordinal)).Should().Be(1,
                $"{name} must not be expired and re-written when cookies are already host-scoped");
            Written(cookies(), name).Should().NotContain("domain=");
        }
    }

    [Fact]
    public void SetSessionCookies_keeps_the_protective_attributes_on_the_widened_cookies()
    {
        var (response, cookies) = NewResponse();

        response.SetSessionCookies("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5),
            Options(".nocturne.run"));

        // Widening the domain hands these cookies to every subdomain, including the anonymous
        // share hosts, so the attributes that keep them off script and off cross-site requests
        // matter more here than they did when each cookie was host-only.
        foreach (var name in new[] { AccessName, RefreshName })
        {
            Written(cookies(), name).Should()
                .Contain("httponly", "a session token must stay out of reach of page script")
                .And.Contain("secure")
                .And.Contain("samesite=lax");
        }

        // The flag cookie carries no credential and is read by the frontend, so it alone is not
        // HttpOnly — but it still travels only over HTTPS and only on same-site navigations.
        Written(cookies(), FlagName).Should()
            .NotContain("httponly")
            .And.Contain("secure")
            .And.Contain("samesite=lax");
    }

    [Fact]
    public void SetSessionCookies_carries_the_supplied_access_token_expiry()
    {
        var (response, cookies) = NewResponse();
        var expires = DateTimeOffset.UtcNow.AddMinutes(37);

        response.SetSessionCookies("access", "refresh", expires, Options(".nocturne.run"));

        Written(cookies(), AccessName).Should()
            .Contain(expires.ToString("ddd, dd MMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ClearSessionCookies_deletes_with_the_same_domain_the_write_used()
    {
        var (response, cookies) = NewResponse();

        response.ClearSessionCookies(Options(".nocturne.run"));

        foreach (var name in new[] { AccessName, RefreshName, FlagName })
        {
            var headers = cookies().Where(c => c.StartsWith($"{name}=", StringComparison.Ordinal)).ToList();
            headers.Should().HaveCount(2, "sign-out must clear both the widened and the host-scoped cookie");
            headers.Should().AllSatisfy(h => h.Should().Contain("expires=Thu, 01 Jan 1970"));
            headers.Should().ContainSingle(h => h.Contains("domain=.nocturne.run", StringComparison.OrdinalIgnoreCase),
                "a cookie is keyed by domain, so the delete must present the domain the write used");
        }
    }

    [Fact]
    public void ClearSessionCookies_deletes_host_scoped_cookies_when_not_widened()
    {
        var (response, cookies) = NewResponse();

        response.ClearSessionCookies(Options(null));

        foreach (var name in new[] { AccessName, RefreshName, FlagName })
        {
            var headers = cookies().Where(c => c.StartsWith($"{name}=", StringComparison.Ordinal)).ToList();
            headers.Should().ContainSingle();
            headers[0].Should().Contain("expires=Thu, 01 Jan 1970").And.NotContain("domain=");
        }
    }

    [Fact]
    public void ApplyCookieDomainDefaults_widens_both_scopes_from_the_base_domain()
    {
        var options = new OidcOptions();

        SessionCookieExtensions.ApplyCookieDomainDefaults(options, "nocturne.run:1612");

        options.Cookie.SessionDomain.Should().Be(".nocturne.run");
        options.Cookie.StateDomain.Should().Be(".nocturne.run",
            "a login begun on a reserved dashboard slug has no slug in its state to be bounced " +
            "back by, so its state cookie has to be readable at the apex callback");
    }

    [Fact]
    public void ApplyCookieDomainDefaults_lets_the_operators_cookie_domain_win_for_state()
    {
        var options = new OidcOptions
        {
            Cookie = new CookieSettings { Domain = ".ops.example.com" },
        };

        SessionCookieExtensions.ApplyCookieDomainDefaults(options, "nocturne.run");

        options.Cookie.StateDomain.Should().Be(".ops.example.com",
            "an operator who set Cookie.Domain already scoped the state cookies deliberately");
        options.Cookie.SessionDomain.Should().Be(".nocturne.run",
            "session scope stays derived from the base domain, not a side effect of that knob");
    }

    [Fact]
    public void ApplyCookieDomainDefaults_leaves_both_host_scoped_where_widening_is_unsafe()
    {
        var options = new OidcOptions();

        SessionCookieExtensions.ApplyCookieDomainDefaults(options, "nocturne.localhost:1612");

        options.Cookie.SessionDomain.Should().BeNull();
        options.Cookie.StateDomain.Should().BeNull();
    }

    [Fact]
    public void ApplyCookieDomainDefaults_never_overwrites_an_explicit_value()
    {
        var options = new OidcOptions
        {
            Cookie = new CookieSettings { SessionDomain = ".a.example", StateDomain = ".b.example" },
        };

        SessionCookieExtensions.ApplyCookieDomainDefaults(options, "nocturne.run");

        options.Cookie.SessionDomain.Should().Be(".a.example");
        options.Cookie.StateDomain.Should().Be(".b.example");
    }

    [Fact]
    public void SetStateCookie_stamps_the_state_domain_so_the_apex_callback_can_read_it()
    {
        var (response, cookies) = NewResponse();
        var options = new OidcOptions
        {
            Cookie = new CookieSettings { StateDomain = ".nocturne.run", Secure = true },
        };

        response.SetStateCookie(".Nocturne.OidcState", "abc", DateTimeOffset.UtcNow.AddMinutes(15), options);

        // The registered redirect_uri is the apex callback. A login begun on any other host —
        // a tenant subdomain, or a reserved dashboard slug that has no slug in its state for
        // OidcCallbackRedirectMiddleware to bounce back to — must present this cookie there.
        Written(cookies(), ".Nocturne.OidcState").Should()
            .Contain("domain=.nocturne.run")
            .And.Contain("httponly")
            .And.Contain("secure")
            .And.Contain("samesite=lax");
    }

    [Fact]
    public void SetStateCookie_expires_the_host_scoped_cookie_it_replaces()
    {
        var (response, cookies) = NewResponse();
        var options = new OidcOptions
        {
            Cookie = new CookieSettings { StateDomain = ".nocturne.run", Secure = true },
        };

        response.SetStateCookie(".Nocturne.OidcState", "abc", DateTimeOffset.UtcNow.AddMinutes(15), options);

        var headers = cookies().Where(c => c.StartsWith(".Nocturne.OidcState=", StringComparison.Ordinal)).ToList();
        headers.Should().HaveCount(2, "a browser holding the pre-widening host-only cookie sends both");
        headers.Single(h => !h.Contains("domain=", StringComparison.OrdinalIgnoreCase))
            .Should().Contain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public void SetStateCookie_stays_host_scoped_when_no_state_domain_is_resolved()
    {
        var (response, cookies) = NewResponse();
        var options = new OidcOptions { Cookie = new CookieSettings { StateDomain = null } };

        response.SetStateCookie(".Nocturne.OidcState", "abc", DateTimeOffset.UtcNow.AddMinutes(15), options);

        cookies().Should().ContainSingle().Which.Should().NotContain("domain=");
    }

    [Fact]
    public void ClearStateCookie_deletes_both_scopes_so_single_use_state_is_really_single_use()
    {
        var (response, cookies) = NewResponse();
        var options = new OidcOptions { Cookie = new CookieSettings { StateDomain = ".nocturne.run" } };

        response.ClearStateCookie(".Nocturne.OidcState", options);

        var headers = cookies().Where(c => c.StartsWith(".Nocturne.OidcState=", StringComparison.Ordinal)).ToList();
        headers.Should().HaveCount(2);
        headers.Should().AllSatisfy(h => h.Should().Contain("expires=Thu, 01 Jan 1970"));
        headers.Should().ContainSingle(h => h.Contains("domain=.nocturne.run", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The live (non-expiring) Set-Cookie header for a cookie name.</summary>
    private static string Written(IReadOnlyList<string> cookies, string name) =>
        cookies.Single(c =>
            c.StartsWith($"{name}=", StringComparison.Ordinal) &&
            !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
