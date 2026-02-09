using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// The core authorization record: "user X approved app Y for scopes Z."
/// User-to-user shares (followers/caregivers) use the same table with grant_type = follower.
/// </summary>
[Table("oauth_grants")]
public class OAuthGrantEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the OAuth client
    /// </summary>
    [Required]
    [Column("client_id")]
    public Guid ClientEntityId { get; set; }

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
    /// For follower grants: the subject ID of the follower receiving access.
    /// Null for app grants. SubjectId remains the data owner.
    /// </summary>
    [Column("follower_subject_id")]
    public Guid? FollowerSubjectId { get; set; }

    /// <summary>
    /// For follower grants created from an invite: the invite ID
    /// </summary>
    [Column("created_from_invite_id")]
    public Guid? CreatedFromInviteId { get; set; }

    /// <summary>
    /// When this grant was revoked (soft delete for audit trail)
    /// </summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When true, data requests using this grant should only return data from
    /// the last 24 hours (rolling window from current request time).
    /// </summary>
    [Column("limit_to_24_hours")]
    public bool LimitTo24Hours { get; set; }

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
    /// The follower subject (for follower grants only)
    /// </summary>
    public SubjectEntity? FollowerSubject { get; set; }

    /// <summary>
    /// The invite this grant was created from (for follower grants only)
    /// </summary>
    public FollowerInviteEntity? CreatedFromInvite { get; set; }

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
    public const string App = OAuthScopes.GrantTypeApp;
    public const string Follower = OAuthScopes.GrantTypeFollower;
}
