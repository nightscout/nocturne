using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One row per passkey challenge a ceremony has been attempted with. A challenge with a row
/// completes no further ceremony.
/// </summary>
/// <remarks>
/// Not tenant-scoped, so no RLS applies: a sign-in completes before any subject is known, and on
/// the apex before any tenant is. The challenge is the random value the ceremony options already
/// sent to the browser, so it is stored as it is. <c>SpentPasskeyChallengeCleanupJob</c> prunes the
/// rows, each after a buffer past <see cref="ExpiresAt"/>.
/// </remarks>
[Table("spent_passkey_challenges")]
public class SpentPasskeyChallengeEntity
{
    /// <summary>The challenge, base64url as the ceremony options carry it.</summary>
    [Key]
    [Column("challenge")]
    public string Challenge { get; set; } = string.Empty;

    /// <summary>When the challenge token stops being accepted, after which the row decides nothing.</summary>
    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }
}
