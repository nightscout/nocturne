using System.Text.RegularExpressions;

namespace Nocturne.Core.Models.Connectors;

/// <summary>
/// The kind of a connector's current error, coarse enough to report outside the tenant. The stored
/// message is free text from a vendor, an exception or a connector, and can name the account or the
/// address it failed on, so only the category it maps to leaves the instance.
/// </summary>
/// <remarks>
/// The mapping reads the message for HTTP status codes, exception names and the phrases the connector
/// base writes (<c>AuthTokenProviderBase</c>, <c>ConnectorBackgroundService</c>). Rules are tried in
/// order and the first match wins, so a status code decides before a word that several kinds share.
/// A message nothing matches is <see cref="Unknown"/>.
/// </remarks>
public static partial class ConnectorErrorCategory
{
    public const string None = "none";
    public const string Auth = "auth";
    public const string RateLimited = "rate_limited";
    public const string Network = "network";
    public const string Upstream = "upstream";
    public const string Config = "config";
    public const string Unknown = "unknown";

    private static readonly (string Category, Regex Pattern)[] Rules =
    [
        (RateLimited, RateLimitedPattern()),
        (Auth, AuthStatusPattern()),
        (Upstream, UpstreamStatusPattern()),
        (Auth, AuthPhrasePattern()),
        (Config, ConfigPattern()),
        (Network, NetworkPattern()),
        (Upstream, UpstreamPhrasePattern()),
    ];

    /// <summary>
    /// The category of a connector's current error. A healthy connector with no message has none; an
    /// unhealthy one with no message is <see cref="Unknown"/>.
    /// </summary>
    public static string Classify(bool isHealthy, string? lastErrorMessage)
    {
        if (string.IsNullOrWhiteSpace(lastErrorMessage))
            return isHealthy ? None : Unknown;

        foreach (var (category, pattern) in Rules)
        {
            if (pattern.IsMatch(lastErrorMessage))
                return category;
        }

        return Unknown;
    }

    [GeneratedRegex(@"\b429\b|too many requests|rate[ -]?limit|throttl", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitedPattern();

    [GeneratedRegex(@"(status|code|http)\D{0,40}\b40[13]\b|\b40[13]\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex AuthStatusPattern();

    [GeneratedRegex(@"(status|code|http)\D{0,40}\b5\d\d\b|\b5\d\d\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex UpstreamStatusPattern();

    [GeneratedRegex(
        @"unauthori[sz]ed|forbidden|did not accept this sign-in|authenticat|credential|password|invalid_grant|access denied|denied data access|rejected the session|session expired|(access|refresh) token",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuthPhrasePattern();

    [GeneratedRegex(
        @"not configured|configuration|must be an http|must stay under|invalid ur[il]|UriFormatException|is required|missing required",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConfigPattern();

    [GeneratedRegex(
        @"SocketException|HttpIOException|no such host|name resolution|name or service not known|connection (refused|reset|closed)|actively refused|timed out|timeout|TaskCanceledException|sync exceeded|\bSSL\b|certificate|network|unreachable",
        RegexOptions.IgnoreCase)]
    private static partial Regex NetworkPattern();

    [GeneratedRegex(
        @"internal server error|bad gateway|service unavailable|could not sign in|no data returned|newer data format|JsonException|invalid start of a value|unexpected response",
        RegexOptions.IgnoreCase)]
    private static partial Regex UpstreamPhrasePattern();
}
