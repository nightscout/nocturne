using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Represents an isolated tenant (patient data silo) in the multitenant system.
/// Each tenant has its own subdomain and isolated clinical data.
/// </summary>
[Table("tenants")]
public class TenantEntity : ISystemTimestamped
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
    /// Whether unauthenticated users can request access to this tenant.
    /// </summary>
    [Column("allow_access_requests")]
    public bool AllowAccessRequests { get; set; } = true;

    /// <summary>
    /// When the onboarding wizard was completed or skipped. Null = not yet onboarded.
    /// </summary>
    [Column("onboarding_completed_at")]
    public DateTime? OnboardingCompletedAt { get; set; }

    /// <summary>Whether this tenant is a demo instance with synthetic data.</summary>
    [Column("is_demo")]
    public bool IsDemo { get; set; }

    /// <summary>
    /// Unguessable token for the tenant's public read-only dashboard, served at
    /// {token}.share.{baseDomain}. Null when public sharing is disabled. Rotating replaces the
    /// value and evicts the resolution cache, so the previous link stops resolving.
    /// </summary>
    [Column("share_token")]
    [MaxLength(32)]
    public string? ShareToken { get; set; }

    /// <summary>When <see cref="ShareToken"/> was last minted or rotated.</summary>
    [Column("share_token_set_at")]
    public DateTime? ShareTokenSetAt { get; set; }

    /// <summary>When the public share link was last accessed. Drives the owner's rotate signal.</summary>
    [Column("share_last_accessed_at")]
    public DateTime? ShareLastAccessedAt { get; set; }

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

    /// <summary>
    /// Demo configuration (null for non-demo tenants).
    /// </summary>
    public TenantDemoConfigEntity? DemoConfig { get; set; }
}
