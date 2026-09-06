using System.Diagnostics.CodeAnalysis;

namespace Nocturne.Connectors.Core.Utilities;

public static class ConnectorUrl
{
    /// <summary>
    ///     <inheritdoc cref="TryResolveBase" path="/summary"/>
    /// </summary>
    /// <param name="url">The URL as stored in the tenant's connector configuration.</param>
    /// <param name="connectorName">Names the connector in the rejection messages.</param>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="url"/> is blank; or carries a scheme other than http/https; or is
    ///     neither, and does not read as a host — a leading slash, a colon introducing something
    ///     other than a port, or anything that will not parse once behind <c>https://</c>.
    /// </exception>
    public static string ResolveBase(string? url, string connectorName)
    {
        // Blank is separated from the rest only to name it in the message.
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"{connectorName} URL is not configured");

        return TryResolveBase(url, out var resolved)
            ? resolved
            : throw new InvalidOperationException($"{connectorName} URL must be http or https");
    }

    /// <summary>
    ///     A tenant-configured instance URL as an absolute origin with no trailing slash. A value
    ///     carrying no scheme of its own is taken for a host and given <c>https</c>, so
    ///     <c>httpbin.example.com</c> resolves to a host and not to a scheme.
    /// </summary>
    public static bool TryResolveBase(string? url, [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            resolved = url.TrimEnd('/');
            return true;
        }

        // Any other explicit scheme is out. Tested before the scheme-less reading, because
        // prepending https:// to "file:/etc/passwd" produces something that still parses.
        if (url.StartsWith('/') || !ColonIntroducesOnlyAPort(url))
            return false;

        var implied = $"https://{url}";
        if (!Uri.TryCreate(implied, UriKind.Absolute, out _))
            return false;

        resolved = implied.TrimEnd('/');
        return true;
    }

    /// <summary>
    ///     A scheme and a <c>host:port</c> cannot be told apart by charset — a scheme may contain the
    ///     dots and dashes a hostname does — so what follows the first colon decides: digits to the end
    ///     of the value or to the first path separator, and it is a port.
    /// </summary>
    internal static bool ColonIntroducesOnlyAPort(string candidate)
    {
        var authority = candidate.AsSpan();

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                return false;

            authority = authority[(close + 1)..];
        }

        var colon = authority.IndexOf(':');
        if (colon < 0)
            return true;

        var afterColon = authority[(colon + 1)..];
        var slash = afterColon.IndexOf('/');
        var port = slash < 0 ? afterColon : afterColon[..slash];

        return port.Length > 0 && !port.ContainsAnyExcept("0123456789");
    }
}
