using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A connector base URL is tenant configuration, so it is supplied by whoever was signed in, and
/// the fetch leaves from inside the deployment's network with its outcome reported back through
/// connector status. The guard sits on every connector HttpClient so the check cannot be skipped
/// by a configuration row that was already stored.
/// </summary>
public class LinkLocalGuardHandlerTests
{
    private static readonly OutboundDestination.AddressResolver Unresolvable =
        (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([]);

    private static OutboundDestination.AddressResolver ResolvesTo(string address) =>
        (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse(address)]);

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://169.254.170.2/v2/credentials")]
    public async Task Refuses_ALinkLocalTarget(string url)
    {
        var client = BuildClient(Unresolvable, out var reachedTransport);

        var act = async () => await client.GetAsync(url);

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*link-local*");
        reachedTransport().Should().BeFalse("the request must not leave the process");
    }

    [Fact]
    public async Task Refuses_AHostnameResolvingToTheMetadataEndpoint()
    {
        var client = BuildClient(ResolvesTo("169.254.169.254"), out var reachedTransport);

        var act = async () => await client.GetAsync("http://metadata.example/token");

        await act.Should().ThrowAsync<HttpRequestException>();
        reachedTransport().Should().BeFalse();
    }

    [Theory]
    [InlineData("http://172.18.0.4:1337/api/v1/entries.json", null)]  // sibling container
    [InlineData("http://192.168.1.50:1337/api/v1/entries.json", null)] // LAN
    [InlineData("http://127.0.0.1:1337/api/v1/entries.json", null)]    // same box
    [InlineData("http://nightscout:1337/api/v1/entries.json", "172.18.0.4")]
    [InlineData("https://mynightscout.example/api/v1/entries.json", "93.184.216.34")]
    public async Task Allows_APrivateOrPublicTarget(string url, string? resolvesTo)
    {
        // Self-hosters point the Nightscout connector at a Nightscout on the same Docker network,
        // the LAN, or localhost. Requiring public routability here would break those installs,
        // which is why this guard is narrower than the webhook policy.
        // An IP literal never reaches the resolver; a hostname does, and must resolve to
        // something, because an unresolvable name fails closed.
        var client = BuildClient(
            resolvesTo is null ? Unresolvable : ResolvesTo(resolvesTo), out var reachedTransport);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        reachedTransport().Should().BeTrue();
    }

    #region Redirects

    // The guard sits above the transport, so it only ever sees the initial RequestUri. With the
    // transport following 3xx itself, an allowed host answering 307 with a link-local Location
    // would be fetched from inside the network beneath the guard, and connector status would
    // report the outcome — the whole check bypassed by one response header. The transport has
    // redirects disabled and the guard follows them itself, re-checking every hop.

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Refuses_ALinkLocalRedirectTarget(HttpStatusCode status)
    {
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://attacker.example/start"] =
                    (status, "http://169.254.169.254/latest/meta-data/iam/security-credentials/"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var act = async () => await client.GetAsync("https://attacker.example/start");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*link-local*");
        // The first hop is allowed; the redirect target must never be requested.
        transport.Requested.Should().Equal(["https://attacker.example/start"]);
    }

    [Fact]
    public async Task Refuses_ALinkLocalTargetReachedThroughSeveralRedirects()
    {
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://a.example/"] = (HttpStatusCode.Found, "https://b.example/"),
                ["https://b.example/"] = (HttpStatusCode.Found, "http://169.254.169.254/"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var act = async () => await client.GetAsync("https://a.example/");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*link-local*");
        transport.Requested.Should().Equal("https://a.example/", "https://b.example/");
    }

    [Fact]
    public async Task Follows_AnHttpToHttpsRedirect()
    {
        // A Nightscout behind an http-to-https redirect is an ordinary config, which is why the
        // fix is per-hop re-checking rather than refusing redirects outright.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["http://mynightscout.example/api/v1/entries.json"] =
                    (HttpStatusCode.MovedPermanently, "https://mynightscout.example/api/v1/entries.json"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var response = await client.GetAsync("http://mynightscout.example/api/v1/entries.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.Requested.Should().Equal(
            "http://mynightscout.example/api/v1/entries.json",
            "https://mynightscout.example/api/v1/entries.json");
    }

    [Fact]
    public async Task Follows_ARelativeRedirect()
    {
        var transport = new ScriptedHandler
        {
            Redirects = { ["https://ns.example/old"] = (HttpStatusCode.Found, "/new") },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var response = await client.GetAsync("https://ns.example/old");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.Requested.Should().Equal("https://ns.example/old", "https://ns.example/new");
    }

    [Fact]
    public async Task DropsCredentialHeaders_WhenARedirectCrossesOrigin()
    {
        // The connector sends the tenant's api-secret. A configured host that redirects elsewhere
        // must not hand it to the redirect target.
        var transport = new ScriptedHandler
        {
            Redirects = { ["https://ns.example/x"] = (HttpStatusCode.Found, "https://elsewhere.example/x") },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://ns.example/x");
        request.Headers.TryAddWithoutValidation("api-secret", "the-tenant-secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer the-token");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        await client.SendAsync(request);

        var followUp = transport.Headers["https://elsewhere.example/x"];
        followUp.Should().NotContainKey("api-secret");
        followUp.Should().NotContainKey("Authorization");
        followUp.Should().ContainKey("Accept", "only credentials are dropped");
    }

    [Fact]
    public async Task KeepsCredentialHeaders_WhenARedirectStaysOnTheSameOrigin()
    {
        var transport = new ScriptedHandler
        {
            Redirects = { ["https://ns.example/old"] = (HttpStatusCode.Found, "https://ns.example/new") },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://ns.example/old");
        request.Headers.TryAddWithoutValidation("api-secret", "the-tenant-secret");

        await client.SendAsync(request);

        transport.Headers["https://ns.example/new"].Should().ContainKey("api-secret");
    }

    [Fact]
    public async Task DropsAnUnknownCredentialHeader_WhenARedirectCrossesOrigin()
    {
        // The header list is an allowlist, not a list of known credential names: a connector added
        // later that authenticates with some header nobody enumerated must not leak it.
        var transport = new ScriptedHandler
        {
            Redirects = { ["https://ns.example/x"] = (HttpStatusCode.Found, "https://elsewhere.example/x") },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://ns.example/x");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "a-future-connectors-secret");
        request.Headers.TryAddWithoutValidation("X-Tenant-Token", "another-one");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        await client.SendAsync(request);

        var followUp = transport.Headers["https://elsewhere.example/x"];
        followUp.Should().NotContainKey("X-Api-Key");
        followUp.Should().NotContainKey("X-Tenant-Token");
        followUp.Should().ContainKey("Accept");
    }

    [Fact]
    public async Task DropsACredentialContentHeader_WhenARedirectCrossesOrigin()
    {
        // Content headers travel on the reused HttpContent instance, so the request-header
        // allowlist never sees them. A signature on one is still a credential: the webhook sender
        // signs exactly this way, and a connector doing the same must not hand it to a host the
        // tenant did not configure.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://ns.example/push"] =
                    (HttpStatusCode.TemporaryRedirect, "https://elsewhere.example/push"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://ns.example/push")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.TryAddWithoutValidation("X-Nocturne-Signature", "the-shared-secret");

        await client.SendAsync(request);

        var followUp = transport.ContentHeaders["https://elsewhere.example/push"];
        followUp.Should().NotContainKey("X-Nocturne-Signature");
        followUp.Should().ContainKey("Content-Type", "the body still has to describe itself");
        transport.Bodies["https://elsewhere.example/push"].Should().Be(
            "{}", "a 307 preserves the body");
    }

    [Fact]
    public async Task KeepsACredentialContentHeader_WhenARedirectStaysOnTheSameOrigin()
    {
        // Positive control for the test above: same-origin is the ordinary trailing-slash or
        // path-rewrite redirect, and stripping there would break signed posts to a configured host.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://ns.example/push"] =
                    (HttpStatusCode.TemporaryRedirect, "https://ns.example/api/push"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://ns.example/push")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.TryAddWithoutValidation("X-Nocturne-Signature", "the-shared-secret");

        await client.SendAsync(request);

        transport.ContentHeaders["https://ns.example/api/push"]
            .Should().ContainKey("X-Nocturne-Signature");
    }

    [Fact]
    public async Task Follows_AnHttpsToHttpDowngrade()
    {
        // HttpClient refuses a secure-to-insecure auto-redirect; this deliberately does not, because
        // plain http is an accepted connector configuration by design, so the downgrade grants
        // nothing configuring http directly would not. The hop is still address-checked.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://ns.example/x"] = (HttpStatusCode.Found, "http://ns.example/x"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var response = await client.GetAsync("https://ns.example/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.Requested.Should().Equal("https://ns.example/x", "http://ns.example/x");
    }

    [Fact]
    public async Task Refuses_AnHttpsToHttpDowngradeOntoALinkLocalAddress()
    {
        // The permissiveness above is about the scheme only — the address check still applies, which
        // is what stops the downgrade being a way in.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://ns.example/x"] = (HttpStatusCode.Found, "http://169.254.169.254/"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var act = async () => await client.GetAsync("https://ns.example/x");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*link-local*");
    }

    [Fact]
    public async Task Stops_AtTheHopCeiling()
    {
        // A redirect loop must terminate rather than spin, and must not be reported as success.
        var transport = new ScriptedHandler { RedirectEverythingTo = "https://loop.example/next" };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var act = async () => await client.GetAsync("https://loop.example/start");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*redirects*");
        transport.Requested.Should().HaveCountLessThan(60,
            "the ceiling bounds the walk rather than letting it run indefinitely");
    }

    [Fact]
    public async Task PreservesTheBody_AcrossTwoConsecutive307s()
    {
        // A 307 clone shares the caller's HttpContent instance, and HttpRequestMessage.Dispose
        // disposes its Content — so disposing hop 1 after building hop 2 previously disposed the
        // body hop 2 was about to send, and could dispose the caller's own content after returning.
        var transport = new ScriptedHandler
        {
            Redirects =
            {
                ["https://a.example/"] = (HttpStatusCode.TemporaryRedirect, "https://b.example/"),
                ["https://b.example/"] = (HttpStatusCode.TemporaryRedirect, "https://c.example/"),
            },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["grant_type"] = "refresh_token" });
        var request = new HttpRequestMessage(HttpMethod.Post, "https://a.example/") { Content = content };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        transport.Requested.Should().Equal("https://a.example/", "https://b.example/", "https://c.example/");
        transport.Bodies.Values.Should().AllBe("grant_type=refresh_token",
            "the body has to survive every hop, not be disposed with the previous request");
    }

    [Fact]
    public async Task DoesNotDisposeTheCallersContent()
    {
        var transport = new ScriptedHandler
        {
            Redirects = { ["https://a.example/"] = (HttpStatusCode.TemporaryRedirect, "https://b.example/") },
        };
        var client = BuildClient(ResolvesTo("93.184.216.34"), transport);

        var content = new StringContent("payload");
        var request = new HttpRequestMessage(HttpMethod.Post, "https://a.example/") { Content = content };

        await client.SendAsync(request);

        // Readable after the call means the caller's content outlived our intermediate disposals.
        var act = async () => await content.ReadAsStringAsync();
        await act.Should().NotThrowAsync("the caller owns its content; we must not dispose it");
    }

    #endregion

    private static HttpClient BuildClient(
        OutboundDestination.AddressResolver resolver, out Func<bool> reachedTransport)
    {
        var transport = new ScriptedHandler();
        reachedTransport = () => transport.Requested.Count > 0;

        var guard = new LinkLocalGuardHandler(NullLogger<LinkLocalGuardHandler>.Instance, resolver)
        {
            InnerHandler = transport,
        };

        return new HttpClient(guard);
    }

    private static HttpClient BuildClient(
        OutboundDestination.AddressResolver resolver, ScriptedHandler transport)
    {
        var guard = new LinkLocalGuardHandler(NullLogger<LinkLocalGuardHandler>.Instance, resolver)
        {
            InnerHandler = transport,
        };

        return new HttpClient(guard);
    }

    /// <summary>
    /// Records every URI it is asked for, and answers a redirect for the ones scripted to.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Location)> Redirects { get; } = [];

        /// <summary>Answers every URI with a redirect, for exercising the hop ceiling.</summary>
        public string? RedirectEverythingTo { get; init; }

        public List<string> Requested { get; } = [];

        public Dictionary<string, Dictionary<string, string>> Headers { get; } = [];

        /// <summary>
        /// Content headers per hop, captured separately because they ride on the
        /// <see cref="HttpContent"/> instance rather than the request.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> ContentHeaders { get; } = [];

        /// <summary>Body as read off the wire per hop, so a disposed body shows up as a failure.</summary>
        public Dictionary<string, string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requested.Add(uri);
            Headers[uri] = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));

            if (request.Content is not null)
            {
                ContentHeaders[uri] = request.Content.Headers
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
                Bodies[uri] = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (RedirectEverythingTo is { } always)
            {
                var looping = new HttpResponseMessage(HttpStatusCode.Found);
                looping.Headers.TryAddWithoutValidation("Location", always);
                return looping;
            }

            if (Redirects.TryGetValue(uri, out var redirect))
            {
                var response = new HttpResponseMessage(redirect.Status);
                response.Headers.TryAddWithoutValidation("Location", redirect.Location);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
