using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for continuous glucose monitor (CGM) readings
/// Maps to Nocturne.Core.Models.V4.SensorGlucose
/// </summary>
[Table("sensor_glucose")]
public class SensorGlucoseEntity : V4TimeSeriesEntityBase, ISyncDedupable, IDeviceAttributedEntity
{
    /// <summary>
    /// FK to the patient's registered device record (resolved at ingest time)
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Stable per-source identifier for synchronization. Unlike <see cref="V4TimeSeriesEntityBase.LegacyId"/> (insert-only),
    /// a record matched by (DataSource, SyncIdentifier) is updated in place on re-import — required so
    /// timezone re-correction can move a reading's timestamp without duplicating it.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double Mgdl { get; set; }

    /// <summary>
    /// CGM trend arrow direction (enum stored as string)
    /// </summary>
    [Column("direction")]
    [MaxLength(32)]
    public string? Direction { get; set; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute
    /// </summary>
    [Column("trend_rate")]
    public double? TrendRate { get; set; }

    /// <summary>
    /// Signal noise level (0-4)
    /// </summary>
    [Column("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// Raw filtered sensor value (scaled ADC)
    /// </summary>
    [Column("filtered")]
    public double? Filtered { get; set; }

    /// <summary>
    /// Raw unfiltered sensor value (scaled ADC)
    /// </summary>
    [Column("unfiltered")]
    public double? Unfiltered { get; set; }

    /// <summary>
    /// Glucose delta in mg/dL over the last 5 minutes
    /// </summary>
    [Column("delta")]
    public double? Delta { get; set; }

    /// <summary>
    /// Whether this reading is smoothed or unsmoothed (enum stored as string). Null when unknown.
    /// </summary>
    [Column("glucose_processing")]
    [MaxLength(16)]
    public string? GlucoseProcessing { get; set; }

    /// <summary>
    /// Smoothed glucose value in mg/dL
    /// </summary>
    [Column("smoothed_mgdl")]
    public double? SmoothedMgdl { get; set; }

    /// <summary>
    /// Unsmoothed (raw) glucose value in mg/dL
    /// </summary>
    [Column("unsmoothed_mgdl")]
    public double? UnsmoothedMgdl { get; set; }
}
