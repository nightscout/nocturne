using System.Diagnostics.CodeAnalysis;
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.API.Helpers;

/// <summary>
/// Addresses a legacy Nightscout from a URL a person typed, which may be served under a sub-path
/// behind a reverse proxy.
/// </summary>
/// <remarks>
/// The address is read by <see cref="NightscoutAddress"/>, as the connector reads it. The base here
/// carries no query: a Nightscout <c>?token=</c> is a credential, so what is stored or shown never
/// holds one. A request to the site is built through <see cref="NightscoutAddress"/>, which keeps
/// the query on every one.
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
        baseUri = NightscoutAddress.TryResolve(nightscoutUrl, out var address) ? new Uri(address.Base + "/") : null;
        return baseUri is not null;
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
