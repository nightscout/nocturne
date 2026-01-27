namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Represents a record of an administered injection dose.
/// </summary>
public class InjectableDose
{
    /// <summary>
    /// Gets or sets the unique identifier for this dose record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the reference to the injectable medication that was administered.
    /// </summary>
    public Guid InjectableMedicationId { get; set; }

    /// <summary>
    /// Gets or sets the amount administered (units or mg based on medication's UnitType).
    /// </summary>
    public double Units { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the dose was administered in Unix milliseconds.
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the optional injection site where the dose was administered.
    /// </summary>
    public InjectionSite? InjectionSite { get; set; }

    /// <summary>
    /// Gets or sets the optional reference to the pen/vial used for this dose.
    /// </summary>
    public Guid? PenVialId { get; set; }

    /// <summary>
    /// Gets or sets the optional lot number for tracking purposes.
    /// </summary>
    public string? LotNumber { get; set; }

    /// <summary>
    /// Gets or sets optional notes about this dose.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets who entered this dose record (e.g., "user", "caregiver", "imported").
    /// </summary>
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Gets or sets the origin system if this dose was imported.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Gets or sets the original identifier from the source system for migration compatibility.
    /// </summary>
    public string? OriginalId { get; set; }
}
