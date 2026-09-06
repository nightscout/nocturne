using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-level alert configuration: scheduled Do Not Disturb. One row per tenant; the row
/// is created on first access. The scheduled DND window is interpreted in the patient's IANA
/// timezone (<see cref="V4.PatientRecordEntity.Timezone"/>) — not stored here. Manual DND is
/// no longer a column here: it is a <c>scope=all</c> row in <c>dnd_windows</c> (ADR 0004 D5),
/// the single source of truth for one-shot mutes; the per-rule
/// <see cref="AlertRuleEntity.AllowThroughDnd"/> bypass applies to both paths.
/// </summary>
[Table("tenant_alert_settings")]
public class TenantAlertSettingsEntity : ITenantScoped
{
    /// <summary>Unique identifier for the row. PK; one row per tenant via the unique index.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Identifier of the tenant this configuration belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    // ---- Scheduled DND ----

    /// <summary>True when a recurring scheduled DND window is configured.</summary>
    [Column("dnd_schedule_enabled")]
    public bool DndScheduleEnabled { get; set; }

    /// <summary>Local-time start of the scheduled DND window (in the patient's timezone).</summary>
    [Column("dnd_schedule_start")]
    public TimeOnly? DndScheduleStart { get; set; }

    /// <summary>Local-time end of the scheduled DND window. Cross-midnight windows are allowed
    /// (start &gt; end interpreted as wrapping over midnight).</summary>
    [Column("dnd_schedule_end")]
    public TimeOnly? DndScheduleEnd { get; set; }

    /// <summary>When the row was created.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the row was last updated.</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
