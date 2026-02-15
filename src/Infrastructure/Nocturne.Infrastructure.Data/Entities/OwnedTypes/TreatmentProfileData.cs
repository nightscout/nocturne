namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// Profile switch data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentProfileData
{
    /// <summary>
    /// Treatment profile
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// JSON string of profile data for profile switches
    /// </summary>
    public string? ProfileJson { get; set; }

    /// <summary>
    /// End profile name for profile switches
    /// </summary>
    public string? EndProfile { get; set; }

    /// <summary>
    /// Whether this is a CircadianPercentageProfile treatment
    /// </summary>
    public bool? CircadianPercentageProfile { get; set; }

    /// <summary>
    /// Percentage for CircadianPercentageProfile
    /// </summary>
    public double? Percentage { get; set; }

    /// <summary>
    /// Timeshift for CircadianPercentageProfile (in hours)
    /// </summary>
    public double? Timeshift { get; set; }

    /// <summary>
    /// Insulin scaling factor for adjustments
    /// </summary>
    public double? InsulinNeedsScaleFactor { get; set; }
}
