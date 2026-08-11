using System.Net;
using FluentAssertions;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.Core.Models.Tests.Net;

/// <summary>
/// The server sends these requests from inside the deployment's network and reports the outcome
/// to the caller who supplied the URL, so a user-supplied URL is a request-forgery primitive.
/// </summary>
/// <remarks>
/// Every case passes an explicit resolver. Relying on the machine's DNS would make the
/// unresolvable-host assertions depend on whether the network wildcards NXDOMAIN — an ISP or
/// resolver that answers every name with a public address flips them — and an uncached lookup is
/// slow enough to matter in a unit suite.
/// </remarks>
public class OutboundDestinationTests
{
    /// <summary>Resolves nothing, as a resolver reports an unknown name.</summary>
    private static readonly OutboundDestination.AddressResolver Unresolvable =
        (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([]);

    private static OutboundDestination.AddressResolver ResolvesTo(params string[] addresses) =>
        (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(
            [.. addresses.Select(IPAddress.Parse)]);

    #region Publicly routable — the alert webhook policy

    [Theory]
    [InlineData("https://93.184.216.34/nocturne")]
    [InlineData("http://93.184.216.34/hook")]
    [InlineData("https://8.8.8.8:8443/hook")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/hook")]
    public async Task PubliclyRoutable_AllowsPublicAddresses(string url) =>
        (await OutboundDestination.IsPubliclyRoutableAsync(url, resolver: Unresolvable))
            .Should().BeTrue();

    [Theory]
    [InlineData("http://127.0.0.1:1610/api/v4/admin/demo/reset")] // loopback: the API itself
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]      // cloud metadata
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.4.2/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://100.100.0.1/")]                            // carrier-grade NAT
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[fd00::1]/")]                              // IPv6 unique-local
    [InlineData("http://192.0.0.1/")]                              // 192.0.0.0/24 protocol assignments
    [InlineData("http://198.18.0.1/")]                             // 198.18.0.0/15 benchmarking
    [InlineData("http://198.19.255.1/")]
    public async Task PubliclyRoutable_RefusesInternalAddresses(string url) =>
        (await OutboundDestination.IsPubliclyRoutableAsync(url, resolver: Unresolvable))
            .Should().BeFalse();

    [Theory]
    [InlineData("http://[::ffff:10.0.0.5]/")]          // IPv4-mapped
    [InlineData("http://[2002:0a00:0005::]/")]         // 6to4 wrapping 10.0.0.5
    [InlineData("http://[2001:0:0:0:0:0:f5ff:fffa]/")] // Teredo wrapping 10.0.0.5 (inverted)
    public async Task PubliclyRoutable_RefusesAPrivateIPv4WrappedInIPv6(string url) =>
        (await OutboundDestination.IsPubliclyRoutableAsync(url, resolver: Unresolvable))
            .Should().BeFalse("an embedded IPv4 address reaches the same host");

    [Fact]
    public async Task PubliclyRoutable_RefusesAHostnameResolvingToAPrivateAddress()
    {
        (await OutboundDestination.IsPubliclyRoutableAsync(
            "https://db.internal/hook", resolver: ResolvesTo("10.1.2.3")))
            .Should().BeFalse();

        (await OutboundDestination.IsPubliclyRoutableAsync(
            "https://split.example/hook", resolver: ResolvesTo("93.184.216.34", "10.1.2.3")))
            .Should().BeFalse("every resolved address has to pass, or the loser is still reachable");
    }

    [Fact]
    public async Task PubliclyRoutable_AllowsAHostnameResolvingOnlyToPublicAddresses() =>
        (await OutboundDestination.IsPubliclyRoutableAsync(
            "https://hooks.example/x", resolver: ResolvesTo("93.184.216.34", "8.8.4.4")))
            .Should().BeTrue();

    #endregion

    #region Not link-local — the connector base-URL policy

    [Fact]
    public async Task NotLinkLocal_AllowsAPrivateAddress()
    {
        // A self-hosted deployment legitimately points the Nightscout connector at a Nightscout
        // on the same Docker network or LAN, so requiring public routability here would break
        // real installs. This policy exists to stop the metadata endpoint, not private networks.
        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://nightscout:1337", resolver: ResolvesTo("172.18.0.4")))
            .Should().BeTrue();

        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://192.168.1.50:1337", resolver: Unresolvable))
            .Should().BeTrue();

        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://localhost:1337", resolver: ResolvesTo("127.0.0.1")))
            .Should().BeTrue("running both on one box is an ordinary local setup");
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://169.254.170.2/v2/credentials")]
    [InlineData("http://[fe80::1]/")]
    public async Task NotLinkLocal_RefusesLinkLocalAddresses(string url) =>
        (await OutboundDestination.IsNotLinkLocalAsync(url, resolver: Unresolvable))
            .Should().BeFalse(
                "no connector has a reason to reach link-local, and that is where cloud credentials live");

    [Fact]
    public async Task NotLinkLocal_RefusesAHostnameResolvingToTheMetadataEndpoint() =>
        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://metadata.example/", resolver: ResolvesTo("169.254.169.254")))
            .Should().BeFalse();

    [Fact]
    public async Task NotLinkLocal_RefusesLinkLocalWrappedInIPv6() =>
        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://[::ffff:169.254.169.254]/", resolver: Unresolvable))
            .Should().BeFalse();

    /// <summary>
    /// The pair is the point: AWS's IPv6 metadata address is unique-local, so refusing it cannot
    /// be done by refusing fc00::/7 without also refusing the self-hosted-Nightscout-on-a-ULA-LAN
    /// case this check exists to keep working.
    /// </summary>
    [Fact]
    public async Task NotLinkLocal_RefusesTheIPv6MetadataEndpointButNotOtherUniqueLocalHosts()
    {
        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://[fd00:ec2::254]/latest/meta-data/iam/security-credentials/",
            resolver: Unresolvable))
            .Should().BeFalse("this serves the same cloud credentials as 169.254.169.254");

        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://[fd12:3456:789a::1]:1337/api/v1/entries.json", resolver: Unresolvable))
            .Should().BeTrue("an ordinary IPv6 ULA LAN host is a supported connector target");
    }

    [Fact]
    public async Task NotLinkLocal_RefusesAHostnameResolvingToTheIPv6MetadataEndpoint() =>
        (await OutboundDestination.IsNotLinkLocalAsync(
            "http://metadata.example/", resolver: ResolvesTo("fd00:ec2::254")))
            .Should().BeFalse();

    #endregion

    #region Shared shape

    [Fact]
    public async Task BothPolicies_RefuseAnUnresolvableHost()
    {
        // Fail closed: a name this process cannot resolve may still be resolvable by the HTTP
        // stack (container DNS, service discovery), so "nothing to check" must deny.
        (await OutboundDestination.IsPubliclyRoutableAsync("https://nowhere.invalid/hook", resolver: Unresolvable))
            .Should().BeFalse();
        (await OutboundDestination.IsNotLinkLocalAsync("https://nowhere.invalid/hook", resolver: Unresolvable))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public async Task BothPolicies_RefuseNonHttpSchemesAndMalformedUrls(string url)
    {
        (await OutboundDestination.IsPubliclyRoutableAsync(url, resolver: Unresolvable))
            .Should().BeFalse();
        (await OutboundDestination.IsNotLinkLocalAsync(url, resolver: Unresolvable))
            .Should().BeFalse();
    }

    #endregion
}
