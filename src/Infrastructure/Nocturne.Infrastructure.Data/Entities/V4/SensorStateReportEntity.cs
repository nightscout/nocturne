using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for the sensor state a device status reported.
/// Maps to Nocturne.Core.Models.V4.SensorStateReport
/// </summary>
[Table("sensor_state_reports")]
public class SensorStateReportEntity : V4TimeSeriesEntityBase
{
    /// <summary>The mapped state, by its member name; null for a value outside the allowlist or an unreadable state.</summary>
    [Column("state")]
    [MaxLength(64)]
    public string? State { get; set; }

    /// <summary>The field the source stated it in.</summary>
    [Column("reported_field")]
    [MaxLength(64)]
    public string ReportedField { get; set; } = null!;

    /// <summary>The value as the source sent it.</summary>
    [Column("reported_value")]
    [MaxLength(128)]
    [FitToColumn]
    public string ReportedValue { get; set; } = null!;
}
