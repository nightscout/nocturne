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

    /// <summary>The live (non-expiring) Set-Cookie header for a cookie name.</summary>
    private static string Written(IReadOnlyList<string> cookies, string name) =>
        cookies.Single(c =>
            c.StartsWith($"{name}=", StringComparison.Ordinal) &&
            !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
}
