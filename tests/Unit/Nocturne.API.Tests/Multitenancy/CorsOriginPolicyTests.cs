using FluentAssertions;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies <see cref="CorsOriginPolicy.IsAllowed"/>, the default CORS policy's origin
/// predicate. Origins are matched against the configured base domain by parsed host so
/// tenant and share subdomains are admitted while look-alike domains are not.
/// </summary>
public sealed class CorsOriginPolicyTests
{
    private const string BaseDomain = "nocturne.run";

    [Theory]
    [InlineData("https://nocturne.run")]                 // apex
    [InlineData("https://acme.nocturne.run")]            // tenant subdomain
    [InlineData("https://ACME.NOCTURNE.RUN")]            // case-insensitive
    [InlineData("https://abc123.share.nocturne.run")]    // public share (two-label) subdomain
    [InlineData("http://acme.nocturne.run")]             // http scheme still matches by host
    public void Allows_the_apex_and_subdomains_of_the_base_domain(string origin)
    {
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.com")]                     // unrelated domain
    [InlineData("https://nocturne.run.evil.com")]        // base domain as a left label of another domain
    [InlineData("https://evilnocturne.run")]             // suffix without a label boundary
    [InlineData("https://notnocturne.run")]              // suffix without a label boundary
    [InlineData("https://nocturne.run.attacker.net")]    // apex embedded, real host elsewhere
    [InlineData("")]                                     // empty origin
    [InlineData("not-a-uri")]                            // unparseable
    [InlineData("ftp://acme.nocturne.run")]              // non-http(s) scheme
    public void Rejects_unrelated_and_look_alike_origins(string origin)
    {
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:5173")]                // SvelteKit dev server
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://acme.localhost:5173")]           // subdomain of localhost
    [InlineData("http://[::1]:5173")]                    // IPv6 loopback
    public void Allows_loopback_origins_only_in_development(string origin)
    {
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: true).Should().BeTrue();
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: false).Should().BeFalse();
    }

    [Fact]
    public void Ignores_a_port_on_the_configured_base_domain()
    {
        // BaseDomain carries a port for local URL construction; the origin host never does.
        CorsOriginPolicy.IsAllowed("https://acme.nocturne.run", "nocturne.run:1612", allowLocalhost: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Rejects_everything_when_the_base_domain_is_empty_and_localhost_is_disallowed()
    {
        CorsOriginPolicy.IsAllowed("https://acme.nocturne.run", "", allowLocalhost: false)
            .Should().BeFalse();
    }

    [Theory]
    // Userinfo spoof: the parsed host is evil.com and userinfo is present — reject.
    [InlineData("http://nocturne.run@evil.com")]
    // Backslash authority: some parsers fold "\" to "/", stranding a path — reject.
    [InlineData("http://nocturne.run\\@evil.com")]
    // Fragment/query smuggling: the base domain only appears after "#"/"?" — reject.
    [InlineData("https://evil.com#.nocturne.run")]
    [InlineData("https://evil.com?.nocturne.run")]
    [InlineData("https://evil.com/.nocturne.run")]
    // Non-http(s) schemes are never real CORS origins — reject.
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("blob:https://acme.nocturne.run/1234")]
    public void Rejects_spoofed_or_non_origin_urls(string origin)
    {
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: false).Should().BeFalse();
    }

    [Theory]
    // A trailing dot on the origin host is a distinct (absolute-DNS) label and does not
    // match the configured base domain. Documenting current behavior: these are rejected.
    [InlineData("https://app.nocturne.run.")]   // trailing-dot subdomain
    [InlineData("https://nocturne.run.")]        // trailing-dot apex
    public void Rejects_trailing_dot_origins(string origin)
    {
        CorsOriginPolicy.IsAllowed(origin, BaseDomain, allowLocalhost: false).Should().BeFalse();
    }

    [Theory]
    // A bare public suffix (or any single-label / empty value) must NOT be usable as a
    // credentialed-CORS base: it would otherwise trust every "*.com" origin.
    [InlineData("https://evil.com", "com")]
    [InlineData("https://anything.com", "com")]
    [InlineData("https://evil.org", "org")]
    [InlineData("https://acme.nocturne.run", "localhost")]  // single-label, no loopback in prod
    public void Rejects_when_the_base_domain_is_a_bare_suffix_or_single_label(string origin, string baseDomain)
    {
        CorsOriginPolicy.IsAllowed(origin, baseDomain, allowLocalhost: false).Should().BeFalse();
    }

    [Theory]
    // Misformatted but well-intentioned base-domain values must normalize to the real host
    // and still admit genuine subdomains (not silently disable cross-origin CORS).
    [InlineData("https://nocturne.run")]   // leading scheme
    [InlineData("http://nocturne.run")]    // leading scheme (http)
    [InlineData("nocturne.run/")]          // trailing slash
    [InlineData("nocturne.run/path")]      // stray path
    [InlineData(".nocturne.run")]          // leading dot
    [InlineData("nocturne.run.")]          // trailing dot
    [InlineData("*.nocturne.run")]         // wildcard prefix
    [InlineData("nocturne.run:1612")]      // port
    public void Normalizes_misformatted_base_domains_and_admits_real_subdomains(string baseDomain)
    {
        CorsOriginPolicy.IsAllowed("https://acme.nocturne.run", baseDomain, allowLocalhost: false)
            .Should().BeTrue();
        CorsOriginPolicy.IsAllowed("https://nocturne.run", baseDomain, allowLocalhost: false)
            .Should().BeTrue();
        // Look-alikes are still rejected after normalization.
        CorsOriginPolicy.IsAllowed("https://evilnocturne.run", baseDomain, allowLocalhost: false)
            .Should().BeFalse();
    }
}
