namespace Nocturne.API.Models.Responses;

/// <summary>
/// Current state of a tenant's single public share link.
/// </summary>
public class ShareLinkDto
{
    /// <summary>Whether a public share link is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>The full share URL when enabled; null otherwise. The raw token is never returned separately.</summary>
    public string? Url { get; set; }

    /// <summary>When true the public view shows full history; when false, only the last 24 hours.</summary>
    public bool FullHistory { get; set; }

    /// <summary>
    /// The data categories anonymous viewers can see, as read-permission atoms (e.g. glucose.read).
    /// A subset of <see cref="Nocturne.Core.Models.Authorization.TenantPermissions.PublicShareScopes"/>.
    /// Empty means the link is live but nothing is shared yet.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>When the share link was last accessed, or null if never (or not yet recorded).</summary>
    public DateTime? LastAccessedAt { get; set; }
}
