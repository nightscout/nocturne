using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Represents an isolated tenant (patient data silo) in the multitenant system.
/// Each tenant has its own subdomain and isolated clinical data.
/// </summary>
[Table("tenants")]
public class TenantEntity
{
    /// <summary>
    /// Unique identifier for the tenant
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Subdomain identifier, e.g. "rhys" for rhys.nocturnecgm.com
    /// </summary>
    [Column("slug")]
    [MaxLength(64)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name
    /// </summary>
    [Column("display_name")]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this tenant is active. Inactive tenants return 403.
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Timestamp of the most recent glucose reading for this tenant.
    /// Updated on every glucose ingest. Used by signal loss detection.
    /// </summary>
    [Column("last_reading_at")]
    public DateTime? LastReadingAt { get; set; }

    /// <summary>
    /// IANA timezone for this tenant (e.g. "America/New_York").
    /// Used for schedule evaluation and display.
    /// </summary>
    [Column("timezone")]
    [MaxLength(64)]
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Preferred name for the person being monitored (e.g. "Alex").
    /// Used in alert payloads. Falls back to DisplayName if null.
    /// </summary>
    [Column("subject_name")]
    [MaxLength(128)]
    public string? SubjectName { get; set; }

    /// <summary>
    /// Whether unauthenticated users can request access to this tenant.
    /// </summary>
    [Column("allow_access_requests")]
    public bool AllowAccessRequests { get; set; } = true;

    /// <summary>
    /// When the onboarding wizard was completed or skipped. Null = not yet onboarded.
    /// </summary>
    [Column("onboarding_completed_at")]
    public DateTime? OnboardingCompletedAt { get; set; }

    /// <summary>
    /// When the tenant record was created
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the tenant record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Collection of members belonging to this tenant
    /// </summary>
    public ICollection<TenantMemberEntity> Members { get; set; } = [];
}
