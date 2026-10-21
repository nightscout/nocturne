using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A submitted record answered but not stored. Maps to <see cref="Core.Models.RejectedUpload"/>.
/// </summary>
[Table("rejected_uploads")]
public class RejectedUploadEntity : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("record_type")]
    [MaxLength(32)]
    public string RecordType { get; set; } = string.Empty;

    [Column("payload", TypeName = "jsonb")]
    public string Payload { get; set; } = "{}";

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

    [Column("received_at")]
    public DateTime ReceivedAt { get; set; }
}
