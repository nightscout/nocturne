using System.Diagnostics.CodeAnalysis;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Nightscout.Services;

/// <summary>
///     Where a tenant's Nightscout answers, read from the URL they gave. A site may be served under a
///     sub-path behind a reverse proxy, and that path can itself be the credential (a proxy that
///     admits only requests under a secret prefix). The URL's query can be one too: a Nightscout
///     <c>?token=</c>, or a key the proxy checks. Every request repeats both. The connector and the
///     import read a site through this one model.
/// </summary>
/// <remarks>
///     User info and fragment are dropped. <see cref="Base"/> and <see cref="Origin"/> carry no
///     credential and may be logged; <see cref="Query"/>, <see cref="Token"/> and
///     <see cref="BaseUri"/> may carry one and must not be stored or shown.
/// </remarks>
public sealed class NightscoutAddress
{
    private NightscoutAddress(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var site = new UriBuilder(uri.Scheme, uri.Host, uri.Port) { Path = path + "/" }.Uri;
        var query = uri.Query.TrimStart('?');

        Base = site.AbsoluteUri.TrimEnd('/');
        Origin = site.GetLeftPart(UriPartial.Authority);
        SocketIoPath = path + "/socket.io";
        Query = query.Length == 0 ? null : query;
        Token = ReadToken(Query);
        BaseUri = Query is null ? site : new Uri($"{site.AbsoluteUri}?{Query}");
    }

    /// <summary>Scheme, host, port and path, with no trailing slash and no query.</summary>
    public string Base { get; }

    /// <summary>
    ///     <see cref="Base"/> with one trailing slash and the URL's query: what an
    ///     <see cref="HttpClient.BaseAddress"/> holds when every request is built through
    ///     <see cref="Of"/> and <see cref="For"/>.
    /// </summary>
    public Uri BaseUri { get; }

    /// <summary>Scheme, host and port.</summary>
    public string Origin { get; }

    /// <summary>The Socket.IO endpoint path, under the site's own path.</summary>
    public string SocketIoPath { get; }

    /// <summary>The URL's query without its <c>?</c>, or null when it has none.</summary>
    public string? Query { get; }

    /// <summary>The Nightscout access token the URL's query carries, or null.</summary>
    public string? Token { get; }

    /// <summary>The address <paramref name="url"/> names, read as <see cref="ConnectorUrl.ResolveBase"/> reads it.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="url"/> is blank or not an http(s) address.</exception>
    public static NightscoutAddress Resolve(string? url) =>
        new(new Uri(ConnectorUrl.ResolveBase(url?.Trim(), "Nightscout")));

    /// <summary>The address <paramref name="url"/> names, or false when it is blank or not an http(s) address.</summary>
    public static bool TryResolve(string? url, [NotNullWhen(true)] out NightscoutAddress? address)
    {
        address = ConnectorUrl.TryResolveBase(url?.Trim(), out var resolved)
                  && Uri.TryCreate(resolved, UriKind.Absolute, out var uri)
            ? new NightscoutAddress(uri)
            : null;
        return address is not null;
    }

    /// <summary>The address an absolute <paramref name="baseUri"/> names, such as a client's <see cref="BaseUri"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="baseUri"/> is not absolute.</exception>
    public static NightscoutAddress Of(Uri baseUri) =>
        baseUri.IsAbsoluteUri
            ? new NightscoutAddress(baseUri)
            : throw new ArgumentException("The base address must be absolute.", nameof(baseUri));

    /// <summary>
    ///     <paramref name="pathAndQuery"/> under <see cref="Base"/>, whether or not it starts with a
    ///     slash, with the URL's own query after any it carries.
    /// </summary>
    public string For(string pathAndQuery)
    {
        var url = $"{Base}/{pathAndQuery.TrimStart('/')}";
        if (Query is null)
            return url;

        return url + (url.Contains('?') ? '&' : '?') + Query;
    }

    private static string? ReadToken(string? query)
    {
        if (query is null)
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            if (equals > 0 && Uri.UnescapeDataString(key) == "token")
                return Uri.UnescapeDataString(pair[(equals + 1)..]) is { Length: > 0 } token ? token : null;
        }

        return null;
    }
}
