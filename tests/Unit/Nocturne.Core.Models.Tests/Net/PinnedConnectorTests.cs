using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Nocturne.Core.Models.Net;
using Xunit;

namespace Nocturne.Core.Models.Tests.Net;

/// <summary>
/// Checking a URL binds a name; the socket binds an address. These cover the join between the two:
/// the address the policy judged has to be the address the connection is made to, or a name that
/// answers differently the second time is reached anyway.
/// </summary>
/// <remarks>
/// Each case drives a real <see cref="HttpClient"/> over a loopback listener, because that is the
/// only way to tell "the pin chose the address" apart from "the machine's resolver happened to
/// agree". The host names used never resolve, so a request that arrives at the listener can only
/// have got there through the pinned address.
/// </remarks>
public class PinnedConnectorTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private int _accepted;

    public PinnedConnectorTests()
    {
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Dispose();
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private static OutboundDestination.AddressResolver ResolvesTo(params string[] addresses) =>
        (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(
            [.. addresses.Select(IPAddress.Parse)]);

    [Fact]
    public async Task ConnectsToTheResolvedAddress_NotWhateverTheNameResolvesToLater()
    {
        // 'pinned.invalid' has no DNS answer at all, so reaching the listener proves the socket was
        // opened to the address the resolver returned rather than to a re-resolution of the name.
        var client = BuildClient(OutboundAddressPolicy.NotLinkLocal, ResolvesTo("127.0.0.1"));

        var response = await client.GetAsync($"http://pinned.invalid:{Port}/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _accepted.Should().Be(1);
    }

    [Fact]
    public async Task Refuses_WhenTheNameFlipsToLinkLocalAfterTheFirstLookup()
    {
        // The rebinding shape: the first lookup is the one a URL check consumes and it answers with
        // something allowed; the connect's lookup answers with the metadata endpoint. Unpinned, the
        // transport would resolve the name itself and this could not be expressed at all.
        var lookups = 0;
        OutboundDestination.AddressResolver flipping = (_, _) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [IPAddress.Parse(++lookups == 1 ? "127.0.0.1" : "169.254.169.254")]);

        _ = await OutboundDestination.IsNotLinkLocalAsync(
            $"http://rebind.invalid:{Port}/", resolver: flipping);

        var client = BuildClient(OutboundAddressPolicy.NotLinkLocal, flipping);
        var act = async () => await client.GetAsync($"http://rebind.invalid:{Port}/");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.ToString().Should().Contain("169.254.169.254");
        _accepted.Should().Be(0, "the connection must not be made at all");
    }

    [Fact]
    public async Task Refuses_WhenOneOfSeveralAnswersIsRefused()
    {
        // Connecting to the allowed one and ignoring the other would make a rebinding answer a
        // matter of ordering luck, and the URL checks already treat a split answer as refused.
        var client = BuildClient(
            OutboundAddressPolicy.NotLinkLocal, ResolvesTo("127.0.0.1", "169.254.169.254"));

        var act = async () => await client.GetAsync($"http://split.invalid:{Port}/");

        await act.Should().ThrowAsync<HttpRequestException>();
        _accepted.Should().Be(0);
    }

    [Fact]
    public async Task Refuses_AnUnresolvableName()
    {
        var client = BuildClient(
            OutboundAddressPolicy.NotLinkLocal,
            (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([]));

        var act = async () => await client.GetAsync($"http://nowhere.invalid:{Port}/");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.ToString().Should().Contain("did not resolve");
    }

    [Fact]
    public async Task Refuses_ALoopbackAddressUnderThePubliclyRoutablePolicy()
    {
        // The policy travels with the connector: the same pinned address the connector policy
        // allows is refused for a webhook. Paired with the positive control above, this is what
        // shows the pin is applying the policy rather than waving everything through.
        var client = BuildClient(OutboundAddressPolicy.PubliclyRoutable, ResolvesTo("127.0.0.1"));

        var act = async () => await client.GetAsync($"http://hooks.invalid:{Port}/");

        await act.Should().ThrowAsync<HttpRequestException>();
        _accepted.Should().Be(0);
    }

    [Fact]
    public async Task Allows_ALoopbackAddressUnderTheConnectorPolicy()
    {
        // Positive control for the case above, and the deliberate difference between the two
        // policies: a connector pointed at a service on the same box is a supported setup.
        var client = BuildClient(OutboundAddressPolicy.NotLinkLocal, ResolvesTo("127.0.0.1"));

        var response = await client.GetAsync($"http://nightscout.invalid:{Port}/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient BuildClient(
        OutboundAddressPolicy policy, OutboundDestination.AddressResolver resolver) =>
        new(new SocketsHttpHandler
        {
            ConnectCallback = new PinnedConnector(policy, resolver).ConnectAsync,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

    /// <summary>Answers every connection with a bare 200 and closes.</summary>
    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref _accepted);

            using (connection)
            {
                var stream = connection.GetStream();
                var request = new byte[4096];
                await stream.ReadAsync(request, _stopping.Token);
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
                    _stopping.Token);
            }
        }
    }
}
