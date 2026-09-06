using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for discrete long-acting basal insulin injection records (MDI).
/// Maps to Nocturne.Core.Models.V4.BasalInjection.
/// </summary>
[Table("basal_injections")]
public class BasalInjectionEntity : V4TimeSeriesEntityBase, ISyncDedupable, IDeviceAttributedEntity
{
    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// FK to the PatientDevice (pen) this injection is attributed to
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Insulin units injected
    /// </summary>
    [Column("units")]
    public double Units { get; set; }

    /// <summary>
    /// Optional user-supplied note.
    /// </summary>
    [Column("notes")]
    [MaxLength(4096)]
    public string? Notes { get; set; }

    /// <summary>
    /// Snapshot of insulin pharmacokinetic settings at injection time (JSONB).
    /// Null when the write carried no PatientInsulin reference — the uploader-client shape,
    /// mirroring <see cref="BolusEntity.InsulinContextJson"/>.
    /// </summary>
    [Column("insulin_context", TypeName = "jsonb")]
    public string? InsulinContextJson { get; set; }
}
