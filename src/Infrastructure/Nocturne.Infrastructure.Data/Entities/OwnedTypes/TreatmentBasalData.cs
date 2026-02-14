namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Basal delivery data associated with a treatment (temp basals, combo bolus).
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentBasalData
{
    /// <summary>
    /// Basal rate (used for temp basal treatments)
    /// </summary>
    public double? Rate { get; set; }

    /// <summary>
    /// Percent of temporary basal rate
    /// </summary>
    public double? Percent { get; set; }

    /// <summary>
    /// Absolute temporary basal rate
    /// </summary>
    public double? Absolute { get; set; }

    /// <summary>
    /// Relative basal rate change
    /// </summary>
    public double? Relative { get; set; }

    /// <summary>
    /// Duration type (e.g., "indefinite")
    /// </summary>
    public string? DurationType { get; set; }

    /// <summary>
    /// End time in milliseconds for duration treatments
    /// </summary>
    public long? EndMills { get; set; }

    /// <summary>
    /// Treatment duration in milliseconds (AAPS uses this alongside the minutes-based Duration field)
    /// </summary>
    public long? DurationInMilliseconds { get; set; }
}
