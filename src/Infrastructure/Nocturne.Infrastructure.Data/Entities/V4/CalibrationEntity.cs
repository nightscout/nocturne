using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for CGM sensor calibration records
/// Maps to Nocturne.Core.Models.V4.Calibration
/// </summary>
[Table("calibrations")]
public class CalibrationEntity : V4TimeSeriesEntityBase
{
    /// <summary>
    /// Calibration slope value
    /// </summary>
    [Column("slope")]
    public double? Slope { get; set; }

    /// <summary>
    /// Calibration intercept value
    /// </summary>
    [Column("intercept")]
    public double? Intercept { get; set; }

    /// <summary>
    /// Calibration scale value
    /// </summary>
    [Column("scale")]
    public double? Scale { get; set; }
}
