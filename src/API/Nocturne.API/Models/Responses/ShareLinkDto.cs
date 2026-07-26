namespace Nocturne.API.Models.Responses;

/// <summary>
/// Current state of a tenant's single public share link.
/// </summary>
public class ShareLinkDto
{
    /// <summary>Whether a public share link is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The full share URL, returned only by the call that generates the link. Null on every other
    /// call, including when <see cref="Enabled"/> is true: only the token's digest is stored, so the
    /// URL cannot be reproduced afterwards. Generating a new link is the only way to see one again.
    /// </summary>
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
