using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Short-lived device codes for the Device Authorization Grant (RFC 8628).
/// Used by headless clients (CLI tools, scripts, IoT devices, pump rigs).
/// </summary>
[Table("oauth_device_codes")]
public class OAuthDeviceCodeEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The client ID that initiated the device flow
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Column("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the device code (the long opaque code the client polls with)
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("device_code_hash")]
    public string DeviceCodeHash { get; set; } = string.Empty;

    /// <summary>
    /// The short user code shown to the user (e.g., "ABCD-1234")
    /// </summary>
    [Required]
    [MaxLength(20)]
    [Column("user_code")]
    public string UserCode { get; set; } = string.Empty;

    /// <summary>
    /// Requested scopes (stored as JSON string array)
    /// </summary>
    [Required]
    [Column("scopes")]
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// When the device code expires (typically 15 minutes)
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the user approved this device code (null if pending)
    /// </summary>
    [Column("approved_at")]
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// When the user denied this device code (null if not denied)
    /// </summary>
    [Column("denied_at")]
    public DateTime? DeniedAt { get; set; }

    /// <summary>
    /// Foreign key to the grant created on approval (null until approved)
    /// </summary>
    [Column("grant_id")]
    public Guid? GrantId { get; set; }

    /// <summary>
    /// Minimum polling interval in seconds (default 5)
    /// </summary>
    [Column("interval")]
    public int Interval { get; set; } = 5;

    /// <summary>
    /// When this record was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this device code has expired
    /// </summary>
    [NotMapped]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether this device code has been approved
    /// </summary>
    [NotMapped]
    public bool IsApproved => ApprovedAt.HasValue;

    /// <summary>
    /// Whether this device code has been denied
    /// </summary>
    [NotMapped]
    public bool IsDenied => DeniedAt.HasValue;

    // Navigation properties

    /// <summary>
    /// The grant created on approval
    /// </summary>
    public OAuthGrantEntity? Grant { get; set; }
}
