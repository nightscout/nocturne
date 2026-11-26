using System.Globalization;
using System.Text.RegularExpressions;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Notifications.External;

/// <summary>
/// What an external notice may hold, and what a sender may link it to.
/// </summary>
public static partial class ExternalNotificationRules
{
    public const int MaxKeyLength = 100;

    public const int MaxTitleLength = 120;

    public const int MaxBodyLength = 500;

    public const int MaxLinkLabelLength = 40;

    public const int MaxLinkUrlLength = 2048;

    /// <summary>How long a notice stays in the tray when the sender names no expiry.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    /// <summary>The latest expiry a sender may name, from the time of the send.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+$")]
    private static partial Regex KeyPattern();

    /// <summary>Whether <paramref name="key"/> is a key a sender may use.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && KeyPattern().IsMatch(key);

    /// <summary>
    /// Checks the request's text, link and expiry, and gives the expiry the notice is stored with.
    /// </summary>
    /// <param name="linkDomains">The domains the sender may link to; empty allows no link.</param>
    /// <returns>The refusal, or null when the request may be sent.</returns>
    public static ExternalNotificationRefusal? Validate(
        ExternalNotificationRequest request,
        IReadOnlyCollection<string> linkDomains,
        DateTime now,
        out DateTime expiresAt)
    {
        expiresAt = now + DefaultLifetime;

        if (!IsValidKey(request.Key))
        {
            return ExternalNotificationRefusal.BadRequest(
                "invalid_key",
                $"The key must be 1 to {MaxKeyLength} letters, digits, '.', '_', ':' or '-'.");
        }

        if (!IsPlainText(request.Title, MaxTitleLength, allowLineBreaks: false))
        {
            return ExternalNotificationRefusal.BadRequest(
                "invalid_title",
                $"The title must be 1 to {MaxTitleLength} characters of text on one line.");
        }

        if (!IsPlainText(request.Body, MaxBodyLength, allowLineBreaks: true))
        {
            return ExternalNotificationRefusal.BadRequest(
                "invalid_body",
                $"The body must be 1 to {MaxBodyLength} characters of text.");
        }

        if (request.Link is { } link)
        {
            if (!IsPlainText(link.Label, MaxLinkLabelLength, allowLineBreaks: false))
            {
                return ExternalNotificationRefusal.BadRequest(
                    "invalid_link",
                    $"The link label must be 1 to {MaxLinkLabelLength} characters of text on one line.");
            }

            if (ParseHttpsUrl(link.Url) is not { } url)
            {
                return ExternalNotificationRefusal.BadRequest(
                    "invalid_link",
                    $"The link must be an absolute https URL of at most {MaxLinkUrlLength} characters, with no user name or password.");
            }

            if (!IsOnDomain(url, linkDomains))
            {
                return ExternalNotificationRefusal.BadRequest(
                    "link_domain_not_allowed",
                    linkDomains.Count == 0
                        ? "This sender has no domain it may link to."
                        : "The link is not on a domain this sender may link to.");
            }
        }

        if (request.ExpiresAt is { } requested)
        {
            var at = requested.UtcDateTime;
            if (at <= now || at > now + MaxLifetime)
            {
                return ExternalNotificationRefusal.BadRequest(
                    "invalid_expiry",
                    $"The expiry must be in the future and at most {MaxLifetime.TotalDays:0} days from now.");
            }

            expiresAt = at;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> has 1 to <paramref name="maxLength"/> characters, is not only
    /// white space, and holds no control character other than a line break where one is allowed.
    /// </summary>
    public static bool IsPlainText(string? text, int maxLength, bool allowLineBreaks)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            return false;

        foreach (var c in text)
        {
            if (allowLineBreaks && c is '\n' or '\r')
                continue;
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The URL when it is an absolute https URL with a host and no user information.</summary>
    public static Uri? ParseHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLinkUrlLength)
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
            return null;
        if (url.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length > 0)
            return null;
        return url;
    }

    /// <summary>
    /// Whether the URL's host is one of <paramref name="domains"/> or a subdomain of one. Hosts and
    /// domains compare in their ASCII form, without case.
    /// </summary>
    public static bool IsOnDomain(Uri url, IEnumerable<string> domains)
    {
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var candidate in domains)
        {
            if (NormalizeDomain(candidate) is not { } domain)
                continue;
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The domains an app may link to: the hosts of its https redirect URIs, leaving out loopback
    /// hosts and IP addresses, which name no site the app owns.
    /// </summary>
    public static IReadOnlyList<string> AppLinkDomains(IEnumerable<string> redirectUris) =>
        redirectUris
            .Select(ParseHttpsUrl)
            .OfType<Uri>()
            .Where(uri => !uri.IsLoopback && uri.HostNameType == UriHostNameType.Dns)
            .Select(uri => uri.IdnHost.TrimEnd('.').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var trimmed = domain.Trim().TrimEnd('.');
        if (trimmed.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null;

        try
        {
            return new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Why a send or a withdrawal was refused, as the HTTP problem the caller gets.</summary>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="Error">The machine-readable code, the problem's <c>error</c> extension.</param>
/// <param name="Detail">What the caller reads.</param>
/// <param name="RetryAfter">For a rate limit, when the caller may send again.</param>
public sealed record ExternalNotificationRefusal(int StatusCode, string Error, string Detail, TimeSpan? RetryAfter = null)
{
    public static ExternalNotificationRefusal BadRequest(string error, string detail) =>
        new(StatusCodes.Status400BadRequest, error, detail);

    public static readonly ExternalNotificationRefusal TenantNotFound =
        new(StatusCodes.Status404NotFound, "tenant_not_found", "No tenant has this id.");

    public static readonly ExternalNotificationRefusal TenantInactive =
        new(StatusCodes.Status409Conflict, "tenant_inactive", "Tenant is not active");
}

/// <summary>A send or a withdrawal: its result, or why it was refused.</summary>
public sealed record ExternalNotificationOutcome<T>(T? Result, ExternalNotificationRefusal? Refusal)
    where T : class
{
    public static ExternalNotificationOutcome<T> Done(T result) => new(result, null);

    public static ExternalNotificationOutcome<T> Refused(ExternalNotificationRefusal refusal) => new(null, refusal);
}
