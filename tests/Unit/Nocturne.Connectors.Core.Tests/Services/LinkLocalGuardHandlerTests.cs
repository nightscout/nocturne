using System.Net;
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

    private static HttpClient BuildClient(
        OutboundDestination.AddressResolver resolver, out Func<bool> reachedTransport)
    {
        var transport = new RecordingHandler();
        reachedTransport = () => transport.WasCalled;

        var guard = new LinkLocalGuardHandler(NullLogger<LinkLocalGuardHandler>.Instance, resolver)
        {
            InnerHandler = transport,
        };

        return new HttpClient(guard);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
