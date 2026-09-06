using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for carb ratio schedule records
/// Maps to Nocturne.Core.Models.V4.CarbRatioSchedule
/// </summary>
[Table("carb_ratio_schedules")]
public class CarbRatioScheduleEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Profile name this carb ratio schedule belongs to
    /// </summary>
    [Column("profile_name")]
    [MaxLength(100)]
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Carb ratio schedule entries stored as JSONB array
    /// </summary>
    [Column("entries_json", TypeName = "jsonb")]
    public string EntriesJson { get; set; } = "[]";
}
