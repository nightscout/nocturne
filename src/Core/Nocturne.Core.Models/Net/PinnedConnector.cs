using System.Net;
using System.Net.Sockets;

namespace Nocturne.Core.Models.Net;

/// <summary>
/// Opens a connection to an address that has just been checked against an
/// <see cref="OutboundAddressPolicy"/>, for use as <see cref="SocketsHttpHandler.ConnectCallback"/>.
/// </summary>
/// <remarks>
/// Checking a URL and then handing that URL to the transport leaves the name to be resolved twice:
/// once for the decision, once for the connect. A short-TTL name that answers with an allowed
/// address for the first lookup and <c>169.254.169.254</c> for the second is reached, and neither
/// resolution is wrong — the check simply did not constrain the socket. Here the resolution and the
/// decision are the same one.
/// <para>
/// The whole connect is refused when <em>any</em> resolved address fails the policy, matching
/// <see cref="OutboundDestination.IsAllowedAsync"/>: a name answering with one allowed and one
/// refused address is a rebinding attempt, not a multi-homed host to failover across.
/// </para>
/// <para>
/// Only the TCP connection is established here. <see cref="SocketsHttpHandler"/> negotiates TLS on
/// top of the returned stream itself, from the request URI — so SNI and certificate validation
/// still see the host name, not the pinned address, and pinning does not weaken https.
/// </para>
/// </remarks>
public sealed class PinnedConnector(
    OutboundAddressPolicy policy,
    OutboundDestination.AddressResolver? resolver = null)
{
    public OutboundAddressPolicy Policy { get; } = policy;

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await OutboundDestination.ResolveAsync(host, cancellationToken, resolver);

        // Fail closed on an empty result, as the URL checks do: a name this process cannot resolve
        // is not a name to hand to the system resolver as a second chance.
        if (addresses.Count == 0)
            throw new HttpRequestException($"Refusing to reach '{host}': the name did not resolve.");

        foreach (var address in addresses)
        {
            if (OutboundDestination.IsAllowed(address, Policy))
                continue;

            throw new HttpRequestException(
                $"Refusing to reach '{host}': it resolves to {address}, which this destination " +
                $"policy ({Policy}) does not permit.");
        }

        return await ConnectToFirstReachableAsync(addresses, port, cancellationToken);
    }

    /// <summary>
    /// Each address is tried in turn, as the transport's own connect would: a v6 answer that
    /// nothing on the path can reach must not take the connection down when a v4 answer beside it
    /// would have worked.
    /// </summary>
    private static async ValueTask<Stream> ConnectToFirstReachableAsync(
        IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(address, port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                (failures ??= []).Add(ex);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw failures is { Count: 1 }
            ? failures[0]
            : new AggregateException($"Could not connect to any address on port {port}.", failures!);
    }
}
