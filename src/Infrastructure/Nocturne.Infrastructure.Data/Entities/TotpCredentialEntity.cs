using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Stores TOTP (Time-based One-Time Password) credentials for two-factor authentication.
/// Credentials are only persisted after the first successful verification.
/// Not tenant-scoped (identity-level, like RecoveryCodeEntity).
/// </summary>
[Table("totp_credentials")]
public class TotpCredentialEntity
{
    /// <summary>
    /// Primary key - UUID Version 7
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the subject (user) who owns this TOTP credential
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// The TOTP secret key used to generate one-time passwords. Stored as a Data Protection
    /// payload via the value converter registered in <c>NocturneDbContext.OnModelCreating</c>
    /// (see <c>TotpSecretProtection</c>); this property always holds the decrypted secret.
    /// </summary>
    [Required]
    [Column("secret_key")]
    public byte[] SecretKey { get; set; } = [];

    /// <summary>
    /// Optional human-readable label for this TOTP credential (e.g. "Authenticator App")
    /// </summary>
    [MaxLength(255)]
    [Column("label")]
    public string? Label { get; set; }

    /// <summary>
    /// When this TOTP credential was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this TOTP credential was last used for authentication
    /// </summary>
    [Column("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// The most recent RFC 6238 time step consumed by a successful verification, or null if the
    /// credential has never been verified. A code at or below this step is rejected, so each
    /// code is usable once rather than for the whole ±1 step acceptance window.
    /// </summary>
    [Column("last_used_step")]
    public long? LastUsedStep { get; set; }

    // Navigation properties

    /// <summary>
    /// The subject (user) who owns this TOTP credential
    /// </summary>
    public SubjectEntity? Subject { get; set; }
}
