namespace Nocturne.Core.Models.V4;

/// <summary>
/// Insulin bolus delivery record
/// </summary>
public class Bolus
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp in Unix milliseconds
    /// </summary>
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that delivered this bolus
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this bolus
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Insulin units delivered
    /// </summary>
    public double Insulin { get; set; }

    /// <summary>
    /// Original programmed dose before any interruption
    /// </summary>
    public double? Programmed { get; set; }

    /// <summary>
    /// Actual insulin delivered, if different from programmed
    /// </summary>
    public double? Delivered { get; set; }

    /// <summary>
    /// Type of bolus delivery (Normal, Square, Dual)
    /// </summary>
    public BolusType? BolusType { get; set; }

    /// <summary>
    /// Whether this bolus was auto-delivered by an APS system
    /// </summary>
    public bool Automatic { get; set; }

    /// <summary>
    /// Duration in minutes for extended/square boluses
    /// </summary>
    public double? Duration { get; set; }
}
