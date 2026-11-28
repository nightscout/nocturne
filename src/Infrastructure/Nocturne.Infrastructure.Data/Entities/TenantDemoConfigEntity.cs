using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Operational configuration for a demo tenant. 1:1 with TenantEntity.
/// Stores demo-specific state that doesn't belong on the core tenant table.
/// </summary>
[Table("tenant_demo_config")]
public class TenantDemoConfigEntity
{
    /// <summary>FK to the tenant this config belongs to (also the PK).</summary>
    [Key]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Navigation to the owning tenant.</summary>
    [ForeignKey(nameof(TenantId))]
    public TenantEntity Tenant { get; set; } = null!;

    /// <summary>When the demo data will next be reset (null if resets disabled).</summary>
    [Column("next_reset_at")]
    public DateTime? NextResetAt { get; set; }

    /// <summary>When the demo data was last reset.</summary>
    [Column("last_reset_at")]
    public DateTime? LastResetAt { get; set; }

    /// <summary>Access mode: open, readonly, authenticated.</summary>
    [Column("access_mode")]
    [MaxLength(32)]
    public string AccessMode { get; set; } = "open";

    /// <summary>Days of historical data to backfill.</summary>
    [Column("backfill_days")]
    public int BackfillDays { get; set; } = 90;

    /// <summary>Interval between real-time data generation (minutes).</summary>
    [Column("interval_minutes")]
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Interval between periodic resets (minutes, 0 = disabled).</summary>
    [Column("reset_interval_minutes")]
    public int ResetIntervalMinutes { get; set; }

    /// <summary>A <see cref="Nocturne.Core.Models.Demo.DemoTenantKind"/>.</summary>
    [Required]
    [MaxLength(16)]
    [Column("kind")]
    public string Kind { get; set; } = Nocturne.Core.Models.Demo.DemoTenantKind.Host;

    /// <summary>On the host, the demo clinic's team, by which provisioning finds the clinic and its persona again.</summary>
    [Column("clinic_team_id")]
    public Guid? ClinicTeamId { get; set; }

    public TeamEntity? ClinicTeam { get; set; }
}
