using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Nocturne.Infrastructure.Data.Entities;

[Table("personal_google_connections")]
[Index(nameof(TenantId), IsUnique = true)]
public class PersonalGoogleConnectionEntity : ITenantScoped
{
    [Key] public Guid Id { get; set; }
    [Column("tenant_id")] public Guid TenantId { get; set; }
    public Guid SubjectId { get; set; }
    public string ProtectedSettings { get; set; } = "";
    public string? ProtectedToken { get; set; }
    [MaxLength(64)] public string? AccountKey { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    [MaxLength(80)] public string? ErrorCode { get; set; }
}

[Table("personal_health_readings")]
[Index(nameof(TenantId), nameof(DataType), nameof(Mills))]
[Index(nameof(TenantId), nameof(DataType), nameof(SourceKey), IsUnique = true)]
public class PersonalHealthReadingEntity : ITenantScoped
{
    [Key] public Guid Id { get; set; }
    [Column("tenant_id")] public Guid TenantId { get; set; }
    [MaxLength(32)] public string DataType { get; set; } = "";
    [MaxLength(64)] public string SourceKey { get; set; } = "";
    public long Mills { get; set; }
    public long? EndMills { get; set; }
    public int? UtcOffsetMinutes { get; set; }
    public decimal Value { get; set; }
    [MaxLength(16)] public string Unit { get; set; } = "";
}
