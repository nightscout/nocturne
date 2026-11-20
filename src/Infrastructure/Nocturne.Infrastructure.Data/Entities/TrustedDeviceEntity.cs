using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One row per browser a subject chose to trust after presenting an authenticator code. While the
/// row is live, a sign-in from that browser whose primary factor passes for the same subject is not
/// asked for a code. The browser holds a random token in a cookie; only its hash is stored, so the
/// table cannot be replayed as cookies.
/// </summary>
/// <remarks>
/// Not tenant-scoped (identity-level, like <see cref="TotpCredentialEntity"/>), so no RLS applies:
/// the trust follows the subject to every tenant it signs in to. Revoking is deleting the row.
/// Expired rows are pruned by <c>TrustedDeviceCleanupJob</c>.
/// </remarks>
[Table("trusted_devices")]
public class TrustedDeviceEntity
{
    /// <summary>
    /// Primary key - UUID Version 7.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the subject the device is trusted for.
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// SHA-256 of the token the browser's cookie carries, as upper-case hex.
    /// </summary>
    [Required]
    [MaxLength(64)]
    [Column("token_hash")]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Short platform label read from the user agent when the device was trusted, shown to the
    /// subject in the list of their trusted devices.
    /// </summary>
    [MaxLength(255)]
    [Column("device_description")]
    public string? DeviceDescription { get; set; }

    /// <summary>
    /// When the subject trusted the device.
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the trust last stood in for an authenticator code, or null if it never has.
    /// </summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// When the trust ends. Fixed when the device is trusted and never extended, so a code is
    /// asked for again after one lifetime however often the device signs in.
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// The subject the device is trusted for.
    /// </summary>
    public SubjectEntity? Subject { get; set; }
}
