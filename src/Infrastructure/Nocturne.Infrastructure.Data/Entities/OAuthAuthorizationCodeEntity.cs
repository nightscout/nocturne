using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Short-lived authorization codes for the Authorization Code + PKCE flow (RFC 7636).
/// Codes are single-use and expire after 10 minutes. The code itself is never stored;
/// only its SHA-256 hash is persisted for verification during token exchange.
/// </summary>
[Table("oauth_authorization_codes")]
public class OAuthAuthorizationCodeEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the OAuth client that initiated the authorization request
    /// </summary>
    [Required]
    [Column("client_entity_id")]
    public Guid ClientEntityId { get; set; }

    /// <summary>
    /// Foreign key to the subject (user) who approved the authorization request
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// SHA-256 hash of the opaque authorization code
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("code_hash")]
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// Approved scopes (stored as JSON string array, e.g. ["entries.read", "treatments.readwrite"])
    /// </summary>
    [Required]
    [Column("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// The redirect URI provided in the authorization request; must match exactly on token exchange
    /// </summary>
    [Required]
    [MaxLength(2000)]
    [Column("redirect_uri")]
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// PKCE S256 code challenge provided in the authorization request
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("code_challenge")]
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>
    /// When this authorization code expires (10 minutes from creation)
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When this code was exchanged for tokens (null until redeemed, prevents replay)
    /// </summary>
    [Column("redeemed_at")]
    public DateTime? RedeemedAt { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this authorization code has expired
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether this authorization code has been redeemed (exchanged for tokens)
    /// </summary>
    [NotMapped]
    public bool IsRedeemed => RedeemedAt.HasValue;

    /// <summary>
    /// Whether this authorization code is currently valid (not expired and not redeemed)
    /// </summary>
    [NotMapped]
    public bool IsValid => !IsExpired && !IsRedeemed;

    // Navigation properties

    /// <summary>
    /// The OAuth client that initiated the authorization request
    /// </summary>
    public OAuthClientEntity? Client { get; set; }

    /// <summary>
    /// The subject (user) who approved the authorization request
    /// </summary>
    public SubjectEntity? Subject { get; set; }
}
