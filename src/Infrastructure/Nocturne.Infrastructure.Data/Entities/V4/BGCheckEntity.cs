using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for blood glucose check records (finger stick or sensor check)
/// Maps to Nocturne.Core.Models.V4.BGCheck
/// </summary>
[Table("bg_checks")]
public class BGCheckEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Glucose value as entered by the user
    /// </summary>
    [Column("glucose")]
    public double Glucose { get; set; }

    /// <summary>
    /// Source type of the glucose reading (enum stored as string: Finger, Sensor)
    /// </summary>
    [Column("glucose_type")]
    [MaxLength(32)]
    public string? GlucoseType { get; set; }

    /// <summary>
    /// Unit of measurement for the glucose value (enum stored as string: MgDl, Mmol)
    /// </summary>
    [Column("units")]
    [MaxLength(32)]
    public string? Units { get; set; }

    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }
}
