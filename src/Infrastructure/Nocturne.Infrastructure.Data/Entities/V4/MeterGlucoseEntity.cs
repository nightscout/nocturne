using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for blood glucose meter readings
/// Maps to Nocturne.Core.Models.V4.MeterGlucose
/// </summary>
[Table("meter_glucose")]
public class MeterGlucoseEntity : V4TimeSeriesEntityBase, IDeviceAttributedEntity
{
    /// <summary>
    /// FK to the PatientDevice (meter) this reading is attributed to
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double Mgdl { get; set; }
}
