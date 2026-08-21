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
    /// <summary>
    /// Number of <c>X-Forwarded-For</c> entries to consume, counted from the right.
    /// </summary>
    /// <remarks>
    /// Raising this above 1 alongside <see cref="KnownProxiesKey"/> or
    /// <see cref="KnownNetworksKey"/> means every intermediate hop must be declared too: the walk
    /// stops at the first address not on the list, so an undeclared middle hop silently costs the
    /// entries beyond it.
    /// </remarks>
    public const string ForwardLimitKey = "ForwardedHeaders:ForwardLimit";

    /// <summary>Comma-separated proxy addresses whose forwarded address is honoured.</summary>
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";

    /// <summary>Comma-separated CIDR ranges whose forwarded address is honoured.</summary>
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

    public static IApplicationBuilder UseNocturneForwardedHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        var logger = app.ApplicationServices
            .GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NocturneForwardedHeadersExtensions));

        app.UseForwardedHeaders(HostAndSchemeOptions());
        app.UseForwardedHeaders(ClientAddressOptions(configuration, logger));
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
    internal static ForwardedHeadersOptions ClientAddressOptions(
        IConfiguration configuration,
        ILogger? logger = null)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = ForwardLimit(configuration),
        };

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        Populate(configuration[KnownProxiesKey], KnownProxiesKey, logger, value =>
        {
            if (!IPAddress.TryParse(value, out var address))
                return false;

            options.KnownProxies.Add(address);
            return true;
        });

        Populate(configuration[KnownNetworksKey], KnownNetworksKey, logger, value =>
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                return false;

            options.KnownIPNetworks.Add(network);
            return true;
        });

        return options;
    }

    private static int ForwardLimit(IConfiguration configuration)
    {
        var configured = configuration[ForwardLimitKey];
        if (string.IsNullOrWhiteSpace(configured))
            return 1;

        if (!int.TryParse(configured, out var limit) || limit < 1)
        {
            throw new InvalidOperationException(
                $"{ForwardLimitKey} is '{configured}', which is not a hop count. It must be a "
                + "positive whole number: how many X-Forwarded-For entries, counted from the "
                + "right, were written by hops you trust.");
        }

        return limit;
    }

    /// <summary>
    /// Parses a declared trust list, refusing to start rather than quietly trusting everyone.
    /// </summary>
    /// <remarks>
    /// These lists exist to stop trusting whoever connects, so a list that parses to nothing does
    /// the opposite of what it was written to do — silently, and to an operator who believes the
    /// peer is pinned. Individual bad entries are dropped loudly; a list with no good entry at all
    /// is a configuration error.
    /// </remarks>
    private static void Populate(
        string? configured,
        string key,
        ILogger? logger,
        Func<string, bool> tryAdd)
    {
        var values = string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (values.Length == 0)
            return;

        var parsed = 0;
        foreach (var value in values)
        {
            if (tryAdd(value))
            {
                parsed++;
            }
            else
            {
                logger?.LogWarning(
                    "Ignoring {ConfigKey} entry '{Value}': it is not an address or range this "
                    + "can match a peer against.",
                    key, value);
            }
        }

        if (parsed == 0)
        {
            throw new InvalidOperationException(
                $"{key} was set to '{configured}', none of which is a usable address or range. "
                + "Leaving it in place would trust whichever peer connects — the opposite of what "
                + "declaring it asks for. Correct the value or remove the setting.");
        }
    }
}
