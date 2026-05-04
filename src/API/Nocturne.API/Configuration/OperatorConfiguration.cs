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

    public OperatorSupportConfiguration Support { get; set; } = new();
}

public class OperatorSupportConfiguration
{
    public OperatorSupportChannelConfiguration? AccountBilling { get; set; }
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
