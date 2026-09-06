using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for carbohydrate intake records
/// Maps to Nocturne.Core.Models.V4.CarbIntake
/// </summary>
[Table("carb_intakes")]
public class CarbIntakeEntity : V4TimeSeriesEntityBase, ISyncDedupable
{
    /// <summary>
    /// Carbohydrates in grams
    /// </summary>
    [Column("carbs")]
    public double Carbs { get; set; }

    /// <summary>
    /// Unique identifier for synchronization across platforms and devices.
    /// </summary>
    [Column("sync_identifier")]
    [MaxLength(256)]
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// The time at which the carbohydrates were consumed according to the device record.
    /// </summary>
    [Column("carb_time")]
    public double? CarbTime { get; set; }

    /// <summary>
    /// Expected duration for carbohydrate absorption in minutes.
    /// </summary>
    [Column("absorption_time")]
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Fat consumed in grams, when the source reports macros
    /// </summary>
    [Column("fat_grams")]
    public double? FatGrams { get; set; }

    /// <summary>
    /// Protein consumed in grams, when the source reports macros
    /// </summary>
    [Column("protein_grams")]
    public double? ProteinGrams { get; set; }
}
