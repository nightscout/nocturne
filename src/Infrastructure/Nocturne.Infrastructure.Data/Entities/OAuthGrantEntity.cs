using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// The core authorization record: "user X approved app Y for scopes Z."
/// User-to-user shares (followers/caregivers) use the same table with grant_type = follower.
/// </summary>
[Table("oauth_grants")]
public class OAuthGrantEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant that owns this grant. Every grant is bound to exactly one tenant
    /// so a token issued on one subdomain is never valid on another.
    /// </summary>
    [Required]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Foreign key to the OAuth client (null for direct grants)
    /// </summary>
    [Column("client_id")]
    public Guid? ClientEntityId { get; set; }

    /// <summary>
    /// Foreign key to the subject (user) who approved this grant
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Type of grant: app (third-party application) or follower (user-to-user sharing)
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("grant_type")]
    public string GrantType { get; set; } = OAuthGrantTypes.App;

    /// <summary>
    /// Granted scopes (stored as JSON string array, e.g. ["entries.read", "treatments.readwrite"])
    /// </summary>
    [Required]
    [Column("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// User-provided friendly name: "Mum's phone", "My xDrip+ on Pixel 9"
    /// </summary>
    [MaxLength(255)]
    [Column("label")]
    public string? Label { get; set; }

    /// <summary>
    /// When this grant was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When a token from this grant was last used
    /// </summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// IP address of last request using this grant
    /// </summary>
    [MaxLength(45)]
    [Column("last_used_ip")]
    public string? LastUsedIp { get; set; }

    /// <summary>
    /// User agent of last request using this grant
    /// </summary>
    [Column("last_used_user_agent")]
    public string? LastUsedUserAgent { get; set; }

    /// <summary>
    /// When this grant was revoked (soft delete for audit trail)
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When this grant expires. Only used for guest grants (creation + 48h).
    /// </summary>
    [Column("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When a guest grant was first activated (code redeemed).
    /// Null means the code hasn't been used yet.
    /// </summary>
    [Column("activated_at")]
    public DateTime? ActivatedAt { get; set; }

    /// <summary>
    /// IP address of the request that activated this guest grant.
    /// </summary>
    [MaxLength(45)]
    [Column("activated_ip")]
    public string? ActivatedIp { get; set; }

    /// <summary>
    /// User-Agent of the request that activated this guest grant.
    /// </summary>
    [Column("activated_user_agent")]
    public string? ActivatedUserAgent { get; set; }

    /// <summary>
    /// Subject ID of the member who created this grant (for guest grants created by
    /// a family member on behalf of the data owner).
    /// </summary>
    [Column("created_by_subject_id")]
    public Guid? CreatedBySubjectId { get; set; }

    /// <summary>
    /// SHA-256 hash of the plaintext token (for direct grants only).
    /// Used to look up grants by token without storing the plaintext.
    /// </summary>
    [AuditRedacted]
    [MaxLength(128)]
    [Column("token_hash")]
    public string? TokenHash { get; set; }

    /// <summary>
    /// SHA-1 hex hash of a legacy Nightscout API secret.
    /// Only populated for migration-seeded grants to enable zero-friction backward compatibility.
    /// </summary>
    [Column("legacy_secret_hash")]
    [MaxLength(128)]
    [AuditRedacted]
    public string? LegacySecretHash { get; set; }

    /// <summary>
    /// Whether this grant has been revoked
    /// </summary>
    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    // Navigation properties

    /// <summary>
    /// The OAuth client this grant is for
    /// </summary>
    public OAuthClientEntity? Client { get; set; }

    /// <summary>
    /// The subject (user) who approved this grant
    /// </summary>
    public SubjectEntity? Subject { get; set; }

    /// <summary>
    /// The member who created this grant (may differ from Subject for guest grants).
    /// </summary>
    public SubjectEntity? CreatedBy { get; set; }

    /// <summary>
    /// Refresh tokens issued under this grant
    /// </summary>
    public ICollection<OAuthRefreshTokenEntity> RefreshTokens { get; set; } =
        new List<OAuthRefreshTokenEntity>();
}

/// <summary>
/// Grant type constants. References OAuthScopes for the canonical values.
/// </summary>
public static class OAuthGrantTypes
{
    /// <summary>Third-party application grant.</summary>
    public const string App = OAuthScopes.GrantTypeApp;

    /// <summary>User-to-user follower/caregiver sharing grant.</summary>
    public const string Follower = OAuthScopes.GrantTypeFollower;

    /// <summary>Direct token grant (API key style, no OAuth client).</summary>
    public const string Direct = OAuthScopes.GrantTypeDirect;

    /// <summary>Guest grant: temporary read-only access link.</summary>
    public const string Guest = OAuthScopes.GrantTypeGuest;
}
