using FluentAssertions;
using Nocturne.API.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies <see cref="SubdomainParser.Extract"/>, the shared host→slug logic used by both
/// <see cref="TenantResolutionMiddleware"/> and the on-demand TLS authorization endpoint.
/// </summary>
public sealed class SubdomainParserTests
{
    private const string BaseDomain = "nocturne.run";

    [Theory]
    [InlineData("acme.nocturne.run", "acme")]
    [InlineData("ACME.NOCTURNE.RUN", "ACME")]
    [InlineData("acme.nocturne.run:443", "acme")]
    public void Extracts_the_slug_from_a_tenant_subdomain(string host, string expected)
    {
        SubdomainParser.Extract(host, BaseDomain).Should().Be(expected);
    }

    [Theory]
    [InlineData("nocturne.run")]          // apex
    [InlineData("nocturne.run:443")]      // apex with port
    [InlineData("evil.com")]              // foreign domain
    [InlineData("notnocturne.run")]       // suffix that isn't a real subdomain boundary
    [InlineData("")]                      // empty host
    public void Returns_null_when_there_is_no_tenant_subdomain(string host)
    {
        SubdomainParser.Extract(host, BaseDomain).Should().BeNull();
    }

    [Fact]
    public void Ignores_a_port_on_the_base_domain()
    {
        SubdomainParser.Extract("acme.localhost", "localhost:1612").Should().Be("acme");
    }

    [Fact]
    public void Returns_null_when_the_base_domain_is_empty()
    {
        SubdomainParser.Extract("acme.nocturne.run", "").Should().BeNull();
    }
}
