using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One row per step-up token minted after a primary factor was verified, recording the subject the
/// token stands for and whether it has been redeemed. The token handed to the client carries only
/// this row's id, so the subject cannot be asserted by the token itself, and redemption consumes the
/// row, so one token yields at most one session.
/// </summary>
/// <remarks>
/// Not tenant-scoped (identity-level, like <see cref="TotpCredentialEntity"/> and
/// <see cref="RecoveryCodeEntity"/>): the passkey assertion that mints a token is not tenant-scoped
/// either, and tenant membership is checked when the session is issued. Expired rows are pruned by
/// <c>OAuthCodeCleanupService</c>.
/// </remarks>
[Table("totp_step_up_tokens")]
public class TotpStepUpTokenEntity
{
    /// <summary>
    /// Primary key - UUID Version 7. This is the token identifier carried in the protected token.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the subject whose primary factor was verified.
    /// </summary>
    [Required]
    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    /// <summary>
    /// When the token was minted.
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the token stops being redeemable.
    /// </summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the token was redeemed, or null if it has not been. A row with a value here is never
    /// redeemable again.
    /// </summary>
    [Column("consumed_at")]
    public DateTime? ConsumedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// The subject whose primary factor was verified.
    /// </summary>
    public SubjectEntity? Subject { get; set; }
}
