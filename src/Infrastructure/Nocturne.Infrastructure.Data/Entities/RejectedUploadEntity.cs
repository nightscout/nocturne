using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A submitted record answered but not stored, once per record however often it is resent. Maps to
/// <see cref="Core.Models.RejectedUpload"/>.
/// </summary>
[Table("rejected_uploads")]
public class RejectedUploadEntity : ITenantScoped, IAuditable
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("record_type")]
    [MaxLength(32)]
    public string RecordType { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the record's client id, or of its payload when it has none: what makes a resend
    /// the same rejected upload.
    /// </summary>
    [Column("identity")]
    [MaxLength(64)]
    public string Identity { get; set; } = string.Empty;

    [Column("payload", TypeName = "jsonb")]
    public string Payload { get; set; } = "{}";

    /// <summary>The size in bytes of the record as received, before it was bounded for storage.</summary>
    [Column("payload_bytes")]
    public int PayloadBytes { get; set; }

    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    [Column("origin")]
    [MaxLength(256)]
    [FitToColumn]
    public string? Origin { get; set; }

    [Column("app")]
    [MaxLength(256)]
    [FitToColumn]
    public string? App { get; set; }

    [Column("device")]
    [MaxLength(256)]
    [FitToColumn]
    public string? Device { get; set; }

    [Column("occurrences")]
    public int Occurrences { get; set; } = 1;

    [Column("first_seen_at")]
    public DateTime FirstSeenAt { get; set; }

    [Column("last_seen_at")]
    public DateTime LastSeenAt { get; set; }
}
