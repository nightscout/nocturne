using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Nocturne.API.Extensions;

/// <summary>
/// Applies the <c>X-Forwarded-*</c> headers the edge sets, as two independent trust decisions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ForwardedHeadersOptions"/> carries one trusted-proxy list for all four headers, so
/// naming the proxy to settle the client address would also gate the host and scheme on it. Those
/// are not interchangeable: a host that fails to be rewritten collapses the response-cache key onto
/// the gateway's destination host, which is shared by every tenant, whereas an address that fails to
/// be rewritten is merely the peer we already had. Splitting the headers across two runs of the
/// middleware keeps the address strict and the host permissive, so a mis-declared proxy costs audit
/// fidelity rather than tenant isolation.
/// </para>
/// <para>
/// The address run consumes <see cref="ForwardedHeadersOptions.ForwardLimit"/> entries from the
/// right of <c>X-Forwarded-For</c>, so what it resolves is the peer the last hop actually saw, not
/// the leftmost entry a caller can write. One entry is right for an edge that presents exactly one:
/// the bundled Caddy overwrites the header with the client it resolved, and the gateway sets it in
/// run mode. An edge that appends to the caller's chain instead needs the hop count raised, and a
/// deployment that can pin its edge's address should name it — both are configuration, because the
/// answer differs per topology and nothing in the process can infer it.
/// </para>
/// </remarks>
public static class NocturneForwardedHeadersExtensions
{
    /// <summary>Number of <c>X-Forwarded-For</c> entries to consume, counted from the right.</summary>
    public const string ForwardLimitKey = "ForwardedHeaders:ForwardLimit";

    /// <summary>Comma-separated proxy addresses whose forwarded address is honoured.</summary>
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";

    /// <summary>Comma-separated CIDR ranges whose forwarded address is honoured.</summary>
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    public static IApplicationBuilder UseNocturneForwardedHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        app.UseForwardedHeaders(HostAndSchemeOptions());
        app.UseForwardedHeaders(ClientAddressOptions(configuration));
        return app;
    }

    /// <summary>
    /// The public host and scheme, from whichever proxy the request arrived through.
    /// </summary>
    /// <remarks>
    /// Any caller admitted to the API's port can write these, but the API is not published in any
    /// bundle and the values only ever widen what the caller could already reach: the host selects
    /// the tenant the caller is asking for, and authorization runs against that tenant regardless.
    /// </remarks>
    internal static ForwardedHeadersOptions HostAndSchemeOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return options;
    }

    /// <summary>
    /// The calling client's address, which rate-limit partitions and audit rows record.
    /// </summary>
    internal static ForwardedHeadersOptions ClientAddressOptions(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = configuration.GetValue<int?>(ForwardLimitKey) is int limit and > 0
                ? limit
                : 1,
        };

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var value in Split(configuration[KnownProxiesKey]))
        {
            if (IPAddress.TryParse(value, out var address))
                options.KnownProxies.Add(address);
        }

        foreach (var value in Split(configuration[KnownNetworksKey]))
        {
            if (System.Net.IPNetwork.TryParse(value, out var network))
                options.KnownIPNetworks.Add(network);
        }

        return options;
    }

    private static IEnumerable<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
