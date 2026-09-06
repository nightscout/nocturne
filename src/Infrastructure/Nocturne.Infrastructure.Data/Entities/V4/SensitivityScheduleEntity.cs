using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for insulin sensitivity factor schedule records
/// Maps to Nocturne.Core.Models.V4.SensitivitySchedule
/// </summary>
[Table("sensitivity_schedules")]
public class SensitivityScheduleEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Profile name this sensitivity schedule belongs to
    /// </summary>
    [Column("profile_name")]
    [MaxLength(100)]
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Insulin sensitivity factor schedule entries stored as JSONB array
    /// </summary>
    [Column("entries_json", TypeName = "jsonb")]
    public string EntriesJson { get; set; } = "[]";
}
