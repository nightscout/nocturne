namespace Nocturne.API.Models.Responses;

/// <summary>
/// Current state of a tenant's single public share link.
/// </summary>
public class ShareLinkDto
{
    /// <summary>Whether a public share link is currently active.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The full share URL, carrying the token in the clear. Returned only by the call that
    /// generates the link and by the call that reveals it — never by the plain read, so it does
    /// not ride along on every page load of the sharing settings.
    /// </summary>
    /// <remarks>
    /// Null on a reveal when the link predates <c>TenantEntity.ShareTokenEncrypted</c>, or when
    /// the instance has no key to decrypt with. <see cref="CanReveal"/> says which case a caller
    /// is in before it asks.
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>
    /// The share URL with the token replaced by bullets, e.g.
    /// <c>https://••••••••••••••••.share.example.com</c>. Null when no link is active. Lets the
    /// owner see the shape of the link, and that one exists, without the secret crossing the wire.
    /// </summary>
    public string? RedactedUrl { get; set; }

    /// <summary>
    /// Whether the reveal call can produce <see cref="Url"/> for this link. False for a link
    /// minted before the token was kept recoverably, or on an instance with no encryption key —
    /// in both cases regenerating is the only way to see a link, as it was for every link before.
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
