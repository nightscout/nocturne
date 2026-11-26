using System.Diagnostics.CodeAnalysis;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Nightscout.Services;

/// <summary>
///     Where a tenant's Nightscout answers, read from the URL they gave. A site may be served under a
///     sub-path behind a reverse proxy, and that path can itself be the credential (a proxy that
///     admits only requests under a secret prefix). The URL's query can be one too: a Nightscout
///     <c>?token=</c>, or a key the proxy checks. Every request repeats both. Every request to a site
///     is built through this one model.
/// </summary>
/// <remarks>
///     User info and fragment are dropped. Everything after the origin may be the credential, so only
///     <see cref="Origin"/> and <see cref="Shown"/> are free of credentials and may be logged, stored
///     or shown. <see cref="Masked"/> is the form a telemetry span carries. <see cref="Base"/>,
///     <see cref="SocketIoPath"/>, <see cref="Query"/>, <see cref="Token"/> and <see cref="BaseUri"/>
///     may each carry one: the path, the query, or both.
/// </remarks>
public sealed class NightscoutAddress
{
    public const string InvalidUrlMessage = "The Nightscout URL must be an http or https address.";

    public const string OutsideBaseMessage = "The path must stay under the Nightscout URL.";

    /// <summary>A path segment at least this long is cut in a span.</summary>
    private const int LongSegment = 16;

    private static readonly char[] PathStarts = ['/', '\\'];

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
    public string For(string pathAndQuery) => WithQuery($"{Base}/{pathAndQuery.TrimStart('/')}");

    /// <summary>
    ///     <see cref="For"/> for a path a caller supplied. False when <paramref name="pathAndQuery"/>
    ///     resolves anywhere but under <see cref="Base"/>: an absolute URL, or one climbing out with
    ///     <c>..</c>.
    /// </summary>
    public bool TryFor(string pathAndQuery, [NotNullWhen(true)] out Uri? resolved)
    {
        var site = new Uri(Base + "/");
        if (Uri.TryCreate(site, pathAndQuery.TrimStart('/'), out var candidate) && site.IsBaseOf(candidate))
        {
            resolved = new Uri(WithQuery(candidate.GetLeftPart(UriPartial.Query)));
            return true;
        }

        resolved = null;
        return false;
    }

    /// <summary>
    ///     The base of <paramref name="nightscoutUrl"/> with no trailing slash, query, fragment or user
    ///     info. A value that does not read as an address is cut at its query or fragment, and loses
    ///     any user info before its host, which ends at a <c>/</c> or, as <see cref="Uri"/> reads an
    ///     http(s) address, a <c>\</c>. It still carries the path, so it is neither stored nor
    ///     shown: it is what <see cref="Shown"/> and <see cref="Masked"/> shorten and what a migration
    ///     source's digest is taken over.
    /// </summary>
    public static string Display(string nightscoutUrl)
    {
        if (TryResolve(nightscoutUrl, out var address))
            return address.Base;

        var end = nightscoutUrl.IndexOfAny(['?', '#']);
        var kept = end < 0 ? nightscoutUrl : nightscoutUrl[..end];

        var schemeEnd = kept.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var pathStart = kept.IndexOfAny(PathStarts, authorityStart);
        var authorityEnd = pathStart < 0 ? kept.Length : pathStart;
        var at = authorityEnd > authorityStart
            ? kept.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart)
            : -1;
        return at < 0 ? kept : kept[..authorityStart] + kept[(at + 1)..];
    }

    /// <summary>
    ///     <paramref name="nightscoutUrl"/> as it may be stored, shown or logged: the host and port of
    ///     <see cref="Display"/>, followed by <c>/…</c> when a path follows them.
    /// </summary>
    public static string Shown(string nightscoutUrl)
    {
        var hostAndPath = HostAndPath(nightscoutUrl).TrimEnd(PathStarts);
        var pathStart = hostAndPath.IndexOfAny(PathStarts);
        return pathStart < 0 ? hostAndPath : hostAndPath[..pathStart] + "/…";
    }

    /// <summary>
    ///     <paramref name="nightscoutUrl"/> as a telemetry span carries it: the host, port and path of
    ///     <see cref="Display"/> with no scheme, each path segment of <see cref="LongSegment"/> or
    ///     more characters cut to its first four followed by <c>…</c>.
    /// </summary>
    public static string Masked(string nightscoutUrl)
    {
        var hostAndPath = HostAndPath(nightscoutUrl);
        var pathStart = hostAndPath.IndexOf('/');
        if (pathStart < 0)
            return hostAndPath;

        var segments = hostAndPath[pathStart..]
            .Split('/')
            .Select(segment => segment.Length >= LongSegment ? $"{segment[..4]}…" : segment);
        return hostAndPath[..pathStart] + string.Join('/', segments).TrimEnd('/');
    }

    private static string HostAndPath(string nightscoutUrl)
    {
        var display = Display(nightscoutUrl);
        var schemeEnd = display.IndexOf("://", StringComparison.Ordinal);
        return schemeEnd < 0 ? display : display[(schemeEnd + 3)..];
    }

    private string WithQuery(string url) =>
        Query is null ? url : url + (url.Contains('?') ? '&' : '?') + Query;

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
