using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Extensions;
using Nocturne.API.RateLimiting;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// What the API resolves a caller's address to, over the middleware that resolves it.
/// </summary>
/// <remarks>
/// The address reaches audit rows and the unsigned rate-limit partition, so the question these
/// answer is which hop got to choose it. The framework consumes
/// <see cref="ForwardedHeadersOptions.ForwardLimit"/> entries from the right of
/// <c>X-Forwarded-For</c>, so an entry a caller prepends only counts if the edge left room for it.
/// </remarks>
public class ForwardedHeadersPipelineTests
{
    private const string Gateway = "10.0.1.7";
    private const string Client = "203.0.113.4";
    private const string Forged = "6.6.6.6";

    // ── Framework semantics the configuration below relies on ────────────────

    [Fact]
    public void TheFrameworkDefaultForwardLimit_IsOne()
    {
        new ForwardedHeadersOptions().ForwardLimit.Should().Be(1,
            "one entry from the right is what the edge is expected to have written");
    }

    [Fact]
    public async Task WithNoProxyDeclared_TheImmediatePeerIsTrustedWhoeverItIs()
    {
        var resolved = await ResolveAsync(Config(), peer: "198.51.100.9", forwardedFor: Forged);

        resolved.Address.Should().Be(Forged,
            "cleared trust lists skip the known-address check entirely");
    }

    // ── The gateway-shaped request ───────────────────────────────────────────

    [Fact]
    public async Task AnEntryTheEdgeWrote_NamesTheClient()
    {
        var resolved = await ResolveAsync(Config(), peer: Gateway, forwardedFor: Client);

        resolved.Address.Should().Be(Client);
    }

    [Fact]
    public async Task EntriesLeftOfTheEdgesOwn_AreNotConsumed()
    {
        var resolved = await ResolveAsync(
            Config(), peer: Gateway, forwardedFor: $"{Forged}, {Client}");

        resolved.Address.Should().Be(Client,
            "only the rightmost entry is consumed, so prepending buys nothing");
    }

    [Fact]
    public async Task AnUploaderPostingThroughTheEdge_ResolvesItsOwnAddress()
    {
        var resolved = await ResolveAsync(
            Config(),
            peer: Gateway,
            forwardedFor: Client,
            forwardedHost: "acme.example.com",
            forwardedProto: "https",
            path: "/api/v1/entries");

        resolved.Address.Should().Be(Client,
            "a Loop or AAPS upload still records the device's address, not the gateway's");
        resolved.Host.Should().Be("acme.example.com");
        resolved.Scheme.Should().Be("https");
    }

    // ── A declared proxy, for deployments that can pin one ───────────────────

    [Fact]
    public async Task WithAProxyDeclared_AForgedHeaderFromElsewhereIsRefused()
    {
        var resolved = await ResolveAsync(
            Config(knownProxies: Gateway), peer: "198.51.100.9", forwardedFor: Forged);

        resolved.Address.Should().Be("198.51.100.9",
            "a caller reaching the API directly is not the gateway and cannot name itself");
    }

    [Fact]
    public async Task WithAProxyDeclared_TheGatewaysOwnEntryIsStillHonoured()
    {
        var resolved = await ResolveAsync(
            Config(knownProxies: Gateway), peer: Gateway, forwardedFor: Client);

        resolved.Address.Should().Be(Client);
    }

    [Fact]
    public async Task WithANetworkDeclared_AnyPeerInsideItIsHonoured()
    {
        var resolved = await ResolveAsync(
            Config(knownNetworks: "10.0.0.0/16"), peer: Gateway, forwardedFor: Client);

        resolved.Address.Should().Be(Client,
            "container addresses are assigned dynamically, so the range is what can be declared");
    }

    /// <summary>
    /// The reason the host and scheme are applied by a second run of the middleware.
    /// </summary>
    /// <remarks>
    /// One options object carries one trusted-proxy list for all four headers, so declaring a proxy
    /// to settle the address would also gate the host on it — and a host that fails to be rewritten
    /// collapses the response-cache key onto the gateway's destination host, which every tenant
    /// shares.
    /// </remarks>
    [Fact]
    public async Task WithAProxyDeclared_ARefusedPeerStillGetsItsHostAndScheme()
    {
        var resolved = await ResolveAsync(
            Config(knownProxies: Gateway),
            peer: "198.51.100.9",
            forwardedFor: Forged,
            forwardedHost: "acme.example.com",
            forwardedProto: "https");

        resolved.Address.Should().Be("198.51.100.9");
        resolved.Host.Should().Be("acme.example.com");
        resolved.Scheme.Should().Be("https");
    }

    // ── An edge that appends rather than overwrites ──────────────────────────

    [Fact]
    public async Task WithTheHopCountRaised_AnAppendingEdgesChainIsWalked()
    {
        var resolved = await ResolveAsync(
            Config(forwardLimit: "2"), peer: Gateway, forwardedFor: $"{Client}, 172.71.0.4");

        resolved.Address.Should().Be(Client);
    }

    [Fact]
    public async Task WithTheHopCountLeftAtOne_AnAppendingEdgeRecordsItsOwnPeer()
    {
        var resolved = await ResolveAsync(
            Config(), peer: Gateway, forwardedFor: $"{Client}, 172.71.0.4");

        resolved.Address.Should().Be("172.71.0.4",
            "the declared cost of a safe default: fidelity, not trust");
    }

    // ── What the rate limiter partitions on ──────────────────────────────────

    [Fact]
    public async Task TheUnsignedRateLimitPartition_IsTheResolvedAddress()
    {
        var partition = await ResolvePartitionAsync(
            Config(), peer: Gateway, forwardedFor: $"{Forged}, {Client}");

        partition.Should().Be(Client,
            "a caller rotating the header it can write must not rotate its own window");
    }

    [Fact]
    public async Task TheUnsignedRateLimitPartition_DoesNotMoveWithAForgedEntry()
    {
        var first = await ResolvePartitionAsync(
            Config(), peer: Gateway, forwardedFor: $"{Forged}, {Client}");
        var second = await ResolvePartitionAsync(
            Config(), peer: Gateway, forwardedFor: $"198.51.100.9, {Client}");

        first.Should().Be(second);
    }

    // ── Configuration parsing ────────────────────────────────────────────────

    [Fact]
    public void OneUnparsableEntryAmongGoodOnes_IsDropped()
    {
        var options = NocturneForwardedHeadersExtensions.ClientAddressOptions(
            Config(knownProxies: $"not-an-address, {Gateway}", knownNetworks: "nonsense, 10.0.0.0/16"));

        options.KnownProxies.Should().ContainSingle().Which.Should().Be(IPAddress.Parse(Gateway));
        options.KnownIPNetworks.Should().ContainSingle();
    }

    /// <summary>
    /// A trust list that parses to nothing does the opposite of what declaring it asks for.
    /// </summary>
    [Theory]
    [InlineData("nocturne-gateway", null)]
    [InlineData("not-an-address, also-not-one", null)]
    [InlineData(null, "10.0.0.0")]
    public void ATrustListWithNoUsableEntry_RefusesToStart(string? proxies, string? networks)
    {
        var act = () => NocturneForwardedHeadersExtensions.ClientAddressOptions(
            Config(knownProxies: proxies, knownNetworks: networks));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*trust whichever peer connects*");
    }

    [Fact]
    public void AnAbsentHopCount_FallsBackToOne()
    {
        NocturneForwardedHeadersExtensions.ClientAddressOptions(Config())
            .ForwardLimit.Should().Be(1);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    public void ANonsensicalHopCount_RefusesToStart(string configured)
    {
        var act = () => NocturneForwardedHeadersExtensions.ClientAddressOptions(
            Config(forwardLimit: configured));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a hop count*");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static IConfiguration Config(
        string? forwardLimit = null,
        string? knownProxies = null,
        string? knownNetworks = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NocturneForwardedHeadersExtensions.ForwardLimitKey] = forwardLimit,
                [NocturneForwardedHeadersExtensions.KnownProxiesKey] = knownProxies,
                [NocturneForwardedHeadersExtensions.KnownNetworksKey] = knownNetworks,
            })
            .Build();

    private sealed record Resolved(string Address, string Host, string Scheme);

    private static async Task<Resolved> ResolveAsync(
        IConfiguration configuration,
        string peer,
        string? forwardedFor = null,
        string? forwardedHost = null,
        string? forwardedProto = null,
        string path = "/")
    {
        var body = await SendAsync(
            configuration,
            peer,
            context => string.Join(
                '|',
                context.Connection.RemoteIpAddress?.ToString() ?? "none",
                context.Request.Host.Value,
                context.Request.Scheme),
            forwardedFor,
            forwardedHost,
            forwardedProto,
            path);

        var parts = body.Split('|');
        return new Resolved(parts[0], parts[1], parts[2]);
    }

    private static Task<string> ResolvePartitionAsync(
        IConfiguration configuration,
        string peer,
        string? forwardedFor)
    {
        var key = new ClientRateLimitKey(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServiceNames.ConfigKeys.InstanceKey] = "s3cret-instance-key",
            })
            .Build());

        return SendAsync(configuration, peer, key.Resolve, forwardedFor);
    }

    private static async Task<string> SendAsync(
        IConfiguration configuration,
        string peer,
        Func<HttpContext, string> project,
        string? forwardedFor = null,
        string? forwardedHost = null,
        string? forwardedProto = null,
        string path = "/")
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                        await next();
                    });
                    app.UseNocturneForwardedHeaders(configuration);
                    app.Run(context => context.Response.WriteAsync(project(context)));
                }))
            .StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        if (forwardedHost is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
        if (forwardedProto is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);

        var response = await host.GetTestClient().SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }
}
