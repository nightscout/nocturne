using System.Diagnostics.CodeAnalysis;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.API.Helpers;

/// <summary>
/// Addresses a legacy Nightscout from a URL a person typed, which may be served under a sub-path
/// behind a reverse proxy.
/// </summary>
/// <remarks>
/// A value with no scheme is read as a host and given https, as <see cref="ConnectorUrl.TryResolveBase"/>
/// reads the connector's URL. A base whose path lacks a trailing slash loses its last segment when
/// a relative path is resolved against it, and a rooted path discards the base path entirely;
/// either drops the sub-path. User info, query and fragment are dropped: they would otherwise end
/// up inside the path, and a Nightscout <c>?token=</c> is a credential.
/// </remarks>
public static class NightscoutBaseUri
{
    public const string InvalidUrlMessage = "The Nightscout URL must be an http or https address.";

    public const string OutsideBaseMessage = "The path must stay under the Nightscout URL.";

    /// <summary>
    /// The scheme, host, port and path of <paramref name="nightscoutUrl"/>, the path ending in
    /// exactly one slash.
    /// </summary>
    public static bool TryFor(string? nightscoutUrl, [NotNullWhen(true)] out Uri? baseUri)
    {
        baseUri = null;
        if (!ConnectorUrl.TryResolveBase(nightscoutUrl?.Trim(), out var resolved)
            || !Uri.TryCreate(resolved, UriKind.Absolute, out var configured))
            return false;

        baseUri = new UriBuilder(configured.Scheme, configured.Host, configured.Port)
        {
            Path = configured.AbsolutePath.TrimEnd('/') + "/",
        }.Uri;
        return true;
    }

    /// <inheritdoc cref="TryFor" path="/summary"/>
    /// <exception cref="ArgumentException"><paramref name="nightscoutUrl"/> is not an http or https address.</exception>
    public static Uri For(string nightscoutUrl) =>
        TryFor(nightscoutUrl, out var baseUri)
            ? baseUri
            : throw new ArgumentException(InvalidUrlMessage, nameof(nightscoutUrl));

    /// <summary>
    /// <paramref name="pathAndQuery"/> under the base of <paramref name="nightscoutUrl"/>, whether
    /// or not it starts with a slash. A path that resolves anywhere else, such as an absolute URL
    /// or one climbing out with <c>..</c>, is refused.
    /// </summary>
    public static bool TryResolve(string? nightscoutUrl, string pathAndQuery, [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;
        return TryFor(nightscoutUrl, out var baseUri) && TryResolveUnder(baseUri, pathAndQuery, out resolved);
    }

    /// <inheritdoc cref="TryResolve" path="/summary"/>
    /// <exception cref="ArgumentException">
    /// <paramref name="nightscoutUrl"/> is not an http or https address, or <paramref name="pathAndQuery"/>
    /// does not resolve under it.
    /// </exception>
    public static Uri Resolve(string nightscoutUrl, string pathAndQuery) =>
        TryResolveUnder(For(nightscoutUrl), pathAndQuery, out var resolved)
            ? resolved
            : throw new ArgumentException(OutsideBaseMessage, nameof(pathAndQuery));

    private static bool TryResolveUnder(Uri baseUri, string pathAndQuery, [NotNullWhen(true)] out Uri? resolved)
    {
        if (Uri.TryCreate(baseUri, pathAndQuery.TrimStart('/'), out var candidate) && baseUri.IsBaseOf(candidate))
        {
            resolved = candidate;
            return true;
        }

        resolved = null;
        return false;
    }

    /// <summary>
    /// <paramref name="nightscoutUrl"/> as it may be stored or shown: the base with no trailing
    /// slash. A value that does not read as an address is cut at its query or fragment, and loses
    /// any user info before its host.
    /// </summary>
    public static string Display(string nightscoutUrl)
    {
        if (TryFor(nightscoutUrl, out var baseUri))
            return baseUri.AbsoluteUri.TrimEnd('/');

        var end = nightscoutUrl.IndexOfAny(['?', '#']);
        var kept = end < 0 ? nightscoutUrl : nightscoutUrl[..end];

        var schemeEnd = kept.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var pathStart = kept.IndexOf('/', authorityStart);
        var authorityEnd = pathStart < 0 ? kept.Length : pathStart;
        var at = authorityEnd > authorityStart
            ? kept.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart)
            : -1;
        return at < 0 ? kept : kept[..authorityStart] + kept[(at + 1)..];
    }
}
