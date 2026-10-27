using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A known value encrypted with the instance key, so a start with a different key is detected
/// before anything else fails to decrypt. One row, platform-level.
/// </summary>
[Table("instance_key_check")]
public class InstanceKeyCheckEntity : ISystemCreated
{
    public const int SingletonId = 1;

    [Key]
    [Column("id")]
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Changes each time the check is written under a new key, so the notices about one key change
    /// are told apart from those about the next.
    /// </summary>
    [Column("generation")]
    public Guid Generation { get; set; }

    /// <summary>
    /// True from the start that found a changed key until no stored secret is left that the key
    /// cannot decrypt. The check itself is rewritten under the new key at once, so this is what
    /// carries the change across restarts.
    /// </summary>
    [Column("outstanding")]
    public bool Outstanding { get; set; }

    [Required]
    [Column("ciphertext")]
    public string Ciphertext { get; set; } = string.Empty;

    /// <summary>
    /// The check as the key before this one wrote it. A key that reads this but not
    /// <see cref="Ciphertext"/> was replaced, so a replica still running on it does not start a
    /// change of its own and flip the check back.
    /// </summary>
    [Column("previous_ciphertext")]
    public string? PreviousCiphertext { get; set; }

    /// <summary>
    /// When a replica on the current key last scanned for its outstanding change, by the database
    /// clock. A key that reads only <see cref="PreviousCiphertext"/> defers to the current one only
    /// while this is recent: after that, nobody runs the current key, and the older key coming back
    /// is a key change of its own.
    /// </summary>
    [Column("checked_at")]
    public DateTime? CheckedAt { get; set; }

    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }
}
