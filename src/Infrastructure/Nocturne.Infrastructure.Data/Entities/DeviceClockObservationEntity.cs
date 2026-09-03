using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for one piece of device-clock offset evidence. Maps to the
/// device_clock_observations table and to
/// <see cref="Nocturne.Core.Models.Timezones.DeviceClockObservation"/>. Deliberately separate from
/// timezone_timeline: user assertions and derived evidence never share a store.
/// </summary>
[Table("device_clock_observations")]
public class DeviceClockObservationEntity : ITenantScoped
{
    /// <summary>The unique identifier of the tenant this observation belongs to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Primary key — UUID Version 7.</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>Connector the evidence came from (e.g. "glooko").</summary>
    [Column("connector")]
    [MaxLength(32)]
    [Required]
    public string Connector { get; set; } = string.Empty;

    /// <summary>Where the evidence came from (profile assertion vs upload-batch derivation).</summary>
    [Column("source")]
    public int Source { get; set; }

    /// <summary>Real-UTC instant the offset was observed.</summary>
    [Column("observed_at")]
    public DateTime ObservedAt { get; set; }

    /// <summary>Minutes east of UTC the device clock showed.</summary>
    [Column("offset_minutes")]
    public int OffsetMinutes { get; set; }

    /// <summary>True for a two-sided estimate; false for a hard lower bound.</summary>
    [Column("is_estimate")]
    public bool IsEstimate { get; set; }

    /// <summary>Number of records that produced this observation.</summary>
    [Column("sample_count")]
    public int SampleCount { get; set; }

    /// <summary>Earliest real-UTC instant the observation is evidence about (upload batches only).</summary>
    [Column("covers_from")]
    public DateTime? CoversFrom { get; set; }

    /// <summary>The IANA zone the account declared at observation time (profile source only).</summary>
    [Column("declared_timezone")]
    [MaxLength(64)]
    public string? DeclaredTimezone { get; set; }

    /// <summary>When this row was created (UTC).</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
