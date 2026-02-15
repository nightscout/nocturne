namespace Nocturne.Infrastructure.Data.Entities.OwnedTypes;

/// <summary>
/// AndroidAPS-specific data associated with a treatment.
/// EF Core owned type -- stored as columns on the treatments table.
/// </summary>
public class TreatmentAapsData
{
    /// <summary>
    /// AAPS internal pump identifier for linking treatments to pump events
    /// </summary>
    public long? PumpId { get; set; }

    /// <summary>
    /// Serial number of the insulin pump that created this treatment
    /// </summary>
    public string? PumpSerial { get; set; }

    /// <summary>
    /// Type of insulin pump (e.g., "ACCU_CHEK_COMBO", "OMNIPOD_DASH", "VIRTUAL")
    /// </summary>
    public string? PumpType { get; set; }

    /// <summary>
    /// AAPS internal identifier linking to the end event of a duration-based treatment
    /// </summary>
    public long? EndId { get; set; }

    /// <summary>
    /// Whether the treatment is valid. AAPS sets this to false for soft-deleted records.
    /// </summary>
    public bool? IsValid { get; set; }

    /// <summary>
    /// Whether the treatment is read-only and should not be modified by the client
    /// </summary>
    public bool? IsReadOnly { get; set; }

    /// <summary>
    /// Whether this insulin treatment represents basal insulin delivery (vs. bolus)
    /// </summary>
    public bool? IsBasalInsulin { get; set; }

    /// <summary>
    /// Original duration before AAPS modified this treatment
    /// </summary>
    public int? OriginalDuration { get; set; }

    /// <summary>
    /// Original profile name before a profile switch modification
    /// </summary>
    public string? OriginalProfileName { get; set; }

    /// <summary>
    /// Original basal percentage before modification
    /// </summary>
    public int? OriginalPercentage { get; set; }

    /// <summary>
    /// Original timeshift value in hours before modification
    /// </summary>
    public int? OriginalTimeshift { get; set; }

    /// <summary>
    /// Original customized profile name before modification
    /// </summary>
    public string? OriginalCustomizedName { get; set; }

    /// <summary>
    /// Original end timestamp in milliseconds before modification
    /// </summary>
    public long? OriginalEnd { get; set; }
}
