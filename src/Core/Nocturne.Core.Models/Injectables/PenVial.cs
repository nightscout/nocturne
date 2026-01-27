namespace Nocturne.Core.Models.Injectables;

/// <summary>
/// Represents an insulin pen or vial in inventory for tracking purposes.
/// </summary>
public class PenVial
{
    /// <summary>
    /// Gets or sets the unique identifier for this pen/vial.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the reference to the injectable medication this pen/vial contains.
    /// </summary>
    public Guid InjectableMedicationId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this pen/vial was opened in Unix milliseconds.
    /// </summary>
    public long? OpenedAt { get; set; }

    /// <summary>
    /// Gets or sets the expiration timestamp in Unix milliseconds.
    /// Typically calculated as opened date plus 28 days for most insulins.
    /// </summary>
    public long? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the initial units in this pen/vial (e.g., 300u pen, 1000u vial).
    /// </summary>
    public double? InitialUnits { get; set; }

    /// <summary>
    /// Gets or sets the remaining units in this pen/vial.
    /// Decremented when doses are logged.
    /// </summary>
    public double? RemainingUnits { get; set; }

    /// <summary>
    /// Gets or sets the lot number for tracking purposes.
    /// </summary>
    public string? LotNumber { get; set; }

    /// <summary>
    /// Gets or sets the current status of this pen/vial.
    /// </summary>
    public PenVialStatus Status { get; set; }

    /// <summary>
    /// Gets or sets optional notes about this pen/vial.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Gets or sets whether this pen/vial is archived (soft delete).
    /// </summary>
    public bool IsArchived { get; set; }
}
