using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// The escape hatch for the vendor login flows. It only earns its exemption from the factory if
/// what it hands back is pinned and isolated.
/// </summary>
public class OutboundHttpClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateIsolated_PinsTheConnect(bool followRedirects)
    {
        var handler = PrimaryHandlerOf(OutboundHttpClient.CreateIsolated(followRedirects));

        var pin = handler.ConnectCallback?.Target as PinnedConnector;

        pin.Should().NotBeNull(
            "these clients walk their own redirect chains, so the socket is the only place every " +
            "hop is judged");
        pin!.Policy.Should().Be(OutboundAddressPolicy.NotLinkLocal);
        handler.AllowAutoRedirect.Should().Be(followRedirects,
            "CareLink reads each Location to capture its authorization code and Tandem reads the " +
            "final URI, so the caller decides");
    }

    [Fact]
    public void CreateIsolated_GivesEachClientItsOwnCookieJar()
    {
        // A factory client shares its handler, and therefore its cookies, across every caller for
        // the handler's lifetime — two tenants signing in at once would trade session cookies.
        // That sharing is the whole reason these two flows are exempt from the factory.
        var first = PrimaryHandlerOf(OutboundHttpClient.CreateIsolated(followRedirects: true));
        var second = PrimaryHandlerOf(OutboundHttpClient.CreateIsolated(followRedirects: true));

        first.UseCookies.Should().BeTrue();
        first.CookieContainer.Should().NotBeSameAs(second.CookieContainer);
    }

    [Fact]
    public void CreateIsolated_UsesTheSuppliedTransport()
    {
        // The test seam CareLink's constructor exposes. Without it every CareLink test would have
        // to reach the network.
        var fake = new StubHandler();

        var client = OutboundHttpClient.CreateIsolated(followRedirects: false, transport: fake);

        Transport(client).Should().BeSameAs(fake);
    }

    private static SocketsHttpHandler PrimaryHandlerOf(HttpClient client) =>
        Transport(client).Should().BeOfType<SocketsHttpHandler>().Subject;

    /// <summary>
    /// The handler an <see cref="HttpClient"/> was constructed with. Private, and there is no
    /// public way to ask — but a client that does not expose its transport cannot be checked for
    /// having a pinned one either.
    /// </summary>
    private static HttpMessageHandler Transport(HttpClient client) =>
        typeof(HttpMessageInvoker)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType))
            .Select(field => field.GetValue(client) as HttpMessageHandler)
            .FirstOrDefault(handler => handler is not null)
            ?? throw new InvalidOperationException(
                "HttpMessageInvoker no longer holds its handler in a field this can read.");

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());
    }
}
