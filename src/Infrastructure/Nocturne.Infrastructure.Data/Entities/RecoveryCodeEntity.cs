using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Stores hashed recovery codes for break-glass account access.
/// Each subject receives 8 single-use codes when enabling passkey authentication.
/// </summary>
[Table("recovery_codes")]
public class RecoveryCodeEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the subject (user) who owns this recovery code
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Self-describing salted PBKDF2 hash of the recovery code
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("code_hash")]
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// When this code was consumed (null if still available)
    /// </summary>
    [Column("used_at")]
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// When this code was invalidated without being used, such as by the migration that
    /// retired the HMAC-keyed hashes (null if still live)
    /// </summary>
    [Column("invalidated_at")]
    public DateTime? InvalidatedAt { get; set; }

    /// <summary>
    /// When this recovery code was generated
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this code has been consumed
    /// </summary>
    [NotMapped]
    public bool IsUsed => UsedAt.HasValue;

    // Navigation properties

    /// <summary>
    /// The subject (user) who owns this recovery code
    /// </summary>
    public SubjectEntity? Subject { get; set; }
}
