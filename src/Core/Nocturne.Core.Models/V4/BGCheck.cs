namespace Nocturne.Core.Models.V4;

/// <summary>
/// Blood glucose check record (finger stick or sensor check)
/// </summary>
public class BGCheck
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
    /// Device identifier that performed this check
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this check
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
    /// Glucose value as entered by the user
    /// </summary>
    public double Glucose { get; set; }

    /// <summary>
    /// Source type of the glucose reading (Finger, Sensor)
    /// </summary>
    public GlucoseType? GlucoseType { get; set; }

    /// <summary>
    /// Glucose value in mg/dL
    /// </summary>
    public double Mgdl { get; set; }

    /// <summary>
    /// Glucose value in mmol/L (computed from Mgdl)
    /// </summary>
    public double? Mmol { get; set; }

    /// <summary>
    /// Unit of measurement for the glucose value
    /// </summary>
    public GlucoseUnit? Units { get; set; }
}
