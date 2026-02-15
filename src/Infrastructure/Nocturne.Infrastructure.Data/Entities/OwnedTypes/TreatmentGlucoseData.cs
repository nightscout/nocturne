namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Glucose reading data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentGlucoseData
{
    /// <summary>
    /// Glucose value for the treatment
    /// </summary>
    public double? Glucose { get; set; }

    /// <summary>
    /// Glucose type (e.g., "Finger", "Sensor")
    /// </summary>
    public string? GlucoseType { get; set; }

    /// <summary>
    /// Blood glucose value in mg/dL
    /// </summary>
    public double? Mgdl { get; set; }

    /// <summary>
    /// Blood glucose value in mmol/L
    /// </summary>
    public double? Mmol { get; set; }

    /// <summary>
    /// Units (e.g., "mg/dl", "mmol")
    /// </summary>
    public string? Units { get; set; }
}
