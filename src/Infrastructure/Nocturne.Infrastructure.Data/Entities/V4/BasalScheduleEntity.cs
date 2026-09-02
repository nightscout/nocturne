using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for basal rate schedule records
/// Maps to Nocturne.Core.Models.V4.BasalSchedule
/// </summary>
[Table("basal_schedules")]
public class BasalScheduleEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Profile name this basal schedule belongs to
    /// </summary>
    [Column("profile_name")]
    [MaxLength(100)]
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Basal rate schedule entries stored as JSONB array
    /// </summary>
    [Column("entries_json", TypeName = "jsonb")]
    public string EntriesJson { get; set; } = "[]";
}
