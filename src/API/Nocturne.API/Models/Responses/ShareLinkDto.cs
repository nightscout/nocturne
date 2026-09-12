namespace Nocturne.API.Models.Responses;

/// <summary>
/// Current state of a tenant's single public share link.
/// </summary>
public class ShareLinkDto
{
    /// <summary>Whether a public share link is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The full share URL, carrying the token in the clear. Returned only by the calls that
    /// generate and reveal the link, never by the plain read, so it does not ride along on every
    /// page load of the sharing settings.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// The share URL with the token replaced by bullets, e.g.
    /// <c>https://••••••••••••••••.share.example.com</c>. Null when no link is active. Shows the
    /// owner that a link exists, and its shape, without the secret crossing the wire.
    /// </summary>
    public string? RedactedUrl { get; set; }

    /// <summary>
    /// Whether <see cref="Url"/> can be produced for this link. A reveal reports this from what it
    /// actually found; every other call can only read the columns, which cannot see the
    /// changed-key case, so a true here is a guess until a reveal settles it. The cases are listed
    /// on <c>TenantEntity.ShareTokenEncrypted</c>.
    /// </summary>
    public bool CanReveal { get; set; }

    /// <summary>When true the public view shows full history; when false, only the last 24 hours.</summary>
    public bool FullHistory { get; set; }

    /// <summary>
    /// The data categories anonymous viewers can see, as read-permission atoms (e.g. glucose.read).
    /// A subset of <see cref="Nocturne.Core.Models.Authorization.Scope.PublicShareScopes"/>.
    /// Empty means the link is live but nothing is shared yet.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>When the share link was last accessed, or null if never (or not yet recorded).</summary>
    public DateTime? LastAccessedAt { get; set; }
}
