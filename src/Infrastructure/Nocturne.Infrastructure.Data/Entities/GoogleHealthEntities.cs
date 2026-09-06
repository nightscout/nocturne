using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Entities;

[Table("google_health_connections")]
[Index(nameof(TenantId), IsUnique = true)]
public class GoogleHealthConnectionEntity : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("subject_id")]
    public Guid SubjectId { get; set; }

    [Column("protected_settings")]
    public string ProtectedSettings { get; set; } = "";

    [Column("protected_token")]
    public string? ProtectedToken { get; set; }

    [Column("account_key")]
    [MaxLength(64)]
    public string? AccountKey { get; set; }

    [Column("last_sync")]
    public DateTimeOffset? LastSync { get; set; }

    [Column("last_attempt")]
    public DateTimeOffset? LastAttempt { get; set; }

    [Column("next_attempt")]
    public DateTimeOffset? NextAttempt { get; set; }

    [Column("error_code")]
    [MaxLength(80)]
    public string? ErrorCode { get; set; }
}

[Table("google_health_readings")]
[Index(nameof(TenantId), nameof(DataType), nameof(Mills))]
[Index(nameof(TenantId), nameof(DataType), nameof(SourceKey), IsUnique = true)]
public class GoogleHealthReadingEntity : ITenantScoped
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("data_type")]
    [MaxLength(32)]
    public string DataType { get; set; } = "";

    [Column("source_key")]
    [MaxLength(64)]
    public string SourceKey { get; set; } = "";

    [Column("mills")]
    public long Mills { get; set; }

    [Column("end_mills")]
    public long? EndMills { get; set; }

    [Column("utc_offset_minutes")]
    public int? UtcOffsetMinutes { get; set; }

    [Column("value")]
    public decimal Value { get; set; }

    [Column("unit")]
    [MaxLength(16)]
    public string Unit { get; set; } = "";
}
