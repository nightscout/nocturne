using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for target blood glucose range schedule records
/// Maps to Nocturne.Core.Models.V4.TargetRangeSchedule
/// </summary>
[Table("target_range_schedules")]
public class TargetRangeScheduleEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Profile name this target range schedule belongs to
    /// </summary>
    [Column("profile_name")]
    [MaxLength(100)]
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Target range schedule entries stored as JSONB array (TargetRangeEntry[])
    /// </summary>
    [Column("entries_json", TypeName = "jsonb")]
    public string EntriesJson { get; set; } = "[]";
}
