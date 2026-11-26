namespace Nocturne.API.Configuration;

public class OperatorConfiguration
{
    public const string SectionName = "Operator";

    /// <summary>
    /// Operator display name (e.g. "Nocturne.run"). When null, operator features are disabled.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether authenticated users can create their own tenants.
    /// SaaS operators set this to false to gate tenant creation behind billing.
    /// </summary>
    public bool AllowSelfServiceCreation { get; set; } = true;

    /// <summary>
    /// Optional webhook URL for custom slug validation.
    /// When configured, Nocturne POSTs { "slug": "xxx" } and expects { "isValid": bool, "message"?: string }.
    /// </summary>
    public string? SlugValidationWebhookUrl { get; set; }

    /// <summary>
    /// Optional webhook URL for custom username validation.
    /// When configured, Nocturne POSTs { "username": "xxx" } and expects { "isValid": bool, "message"?: string }.
    /// </summary>
    public string? UsernameValidationWebhookUrl { get; set; }

    /// <summary>
    /// Optional webhook URL that delivers account links by email. While it is unset Nocturne sends
    /// no account email, and recovery by email is neither offered nor accepted.
    /// When configured, Nocturne POSTs one JSON object per message and treats any 2xx answer as
    /// delivered:
    /// <code>
    /// {
    ///   "kind": "recovery" | "email_confirmation",
    ///   "email": "the recipient address",
    ///   "link": "https://{slug}.{base domain}/auth/recovery?code=...",
    ///   "expiresAt": "2026-11-21T10:30:00Z",
    ///   "siteSlug": "the site's slug",
    ///   "siteDisplayName": "the site's display name",
    ///   "accountDisplayName": "the display name of the account the link is for"
    /// }
    /// </code>
    /// The link is a bearer secret until it expires: the receiver must send it only to the address
    /// named and must not log it.
    /// </summary>
    public string? AccountLinkWebhookUrl { get; set; }

    /// <summary>
    /// Whether <paramref name="url"/> is unset or an absolute http or https URL, the only webhook
    /// targets a delivery can reach. Checked when the options bind, so a bad value stops startup.
    /// </summary>
    public static bool IsUnsetOrAbsoluteHttpUrl(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    public OperatorSupportConfiguration Support { get; set; } = new();

    /// <summary>
    /// The domains a notice the operator sends to a tenant may link to; a subdomain of one is
    /// allowed too. Empty means the domains of <see cref="OperatorSupportConfiguration.AccountBilling"/>
    /// and <see cref="OperatorSupportConfiguration.AccountPortal"/>.
    /// </summary>
    public List<string> NotificationLinkDomains { get; set; } = [];

    /// <summary>
    /// The domains an operator notice may link to: <see cref="NotificationLinkDomains"/>, or when it is
    /// empty the hosts of the support URLs. Empty when neither is configured, so no link is allowed.
    /// </summary>
    public IReadOnlyList<string> ResolveNotificationLinkDomains()
    {
        if (NotificationLinkDomains.Any(d => !string.IsNullOrWhiteSpace(d)))
            return NotificationLinkDomains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList();

        return new[] { Support.AccountBilling?.Url, Support.AccountPortal?.Url }
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .OfType<string>()
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public class OperatorSupportConfiguration
{
    public OperatorSupportChannelConfiguration? AccountBilling { get; set; }

    public OperatorAccountPortalConfiguration? AccountPortal { get; set; }
}

/// <summary>
/// A page the operator hosts for managing the account. Only ever opened in a browser, so it has
/// no mode: an operator whose <see cref="OperatorSupportConfiguration.AccountBilling"/> is an API
/// endpoint still has somewhere to send a visitor.
/// </summary>
public class OperatorAccountPortalConfiguration
{
    public string Url { get; set; } = "";

    /// <summary>
    /// Optional button/link text. Falls back to "Contact {Operator.Name}".
    /// </summary>
    public string? Label { get; set; }
}

public class OperatorSupportChannelConfiguration
{
    /// <summary>
    /// "Redirect" opens the URL in a new tab. "Api" POSTs the issue payload to the URL.
    /// </summary>
    public OperatorSupportMode Mode { get; set; }

    /// <summary>
    /// The operator's support URL (redirect target or API endpoint).
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Optional button/link text. Falls back to "Contact {Operator.Name}".
    /// </summary>
    public string? Label { get; set; }
}

public enum OperatorSupportMode
{
    Redirect,
    Api,
}
