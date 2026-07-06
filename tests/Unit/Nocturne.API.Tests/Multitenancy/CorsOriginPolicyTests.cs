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
        CorsOriginPolicy.IsAllowed("http://acme.localhost", "localhost:1612", allowLocalhost: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Rejects_everything_when_the_base_domain_is_empty_and_localhost_is_disallowed()
    {
        CorsOriginPolicy.IsAllowed("https://acme.nocturne.run", "", allowLocalhost: false)
            .Should().BeFalse();
    }
}
